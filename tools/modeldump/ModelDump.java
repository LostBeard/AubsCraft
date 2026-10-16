import java.io.*;
import java.lang.reflect.*;
import java.util.*;
import java.util.zip.*;

/**
 * Dumps the geometry of every model class of a Minecraft client jar (unobfuscated, 26.x) as JSON: for each class with a static layer-creating method, the part tree with
 * each part's pose and cubes (texture offset, origin, dimensions, grow, mirror). Reflection only: the jar and its libraries are on the class path.
 * usage: java -cp <jar>;<libs...>;. ModelDump <jar> <out.json> [ClassNameFilter]
 */
public class ModelDump {
    static StringBuilder sb = new StringBuilder();

    static Object field(Object o, String type) throws Exception {
        for (Class<?> c = o.getClass(); c != null; c = c.getSuperclass())
            for (Field f : c.getDeclaredFields())
                if (f.getType().getName().equals(type)) { f.setAccessible(true); return f.get(o); }
        return null;
    }

    static Field findField(Class<?> c, String name) {
        for (; c != null; c = c.getSuperclass())
            for (Field f : c.getDeclaredFields()) if (f.getName().equals(name)) { f.setAccessible(true); return f; }
        return null;
    }

    static Object get(Object o, String name) throws Exception {
        Field f = findField(o.getClass(), name);
        return f == null ? null : f.get(o);
    }

    static String num(Object o) {
        if (o instanceof Float || o instanceof Double) {
            double d = ((Number) o).doubleValue();
            if (d == Math.rint(d)) return String.valueOf((long) d);
            return String.valueOf(d);
        }
        return String.valueOf(o);
    }

    static void dumpPart(String name, Object part, int depth) throws Exception {
        sb.append("{\"name\":\"").append(name).append("\"");
        Object pose = get(part, "partPose");
        if (pose != null) {
            sb.append(",\"pose\":[");
            String[] n = {"x", "y", "z", "xRot", "yRot", "zRot"};
            for (int i = 0; i < n.length; i++) {
                Field f = findField(pose.getClass(), n[i]);
                sb.append(i > 0 ? "," : "").append(f == null ? "0" : num(f.get(pose)));
            }
            sb.append("]");
            // scale (26.x PartPose may carry it)
            Field sx = findField(pose.getClass(), "xScale");
            if (sx != null) {
                Object a = sx.get(pose), b = findField(pose.getClass(), "yScale").get(pose), c = findField(pose.getClass(), "zScale").get(pose);
                if (!num(a).equals("1") || !num(b).equals("1") || !num(c).equals("1")) sb.append(",\"scale\":[").append(num(a)).append(",").append(num(b)).append(",").append(num(c)).append("]");
            }
        }
        sb.append(",\"cubes\":[");
        List<?> cubes = (List<?>) get(part, "cubes");
        boolean first = true;
        for (Object cube : cubes) {
            if (!first) sb.append(",");
            first = false;
            Object origin = get(cube, "origin"), dims = get(cube, "dimensions"), grow = get(cube, "grow"), uv = get(cube, "texCoord");
            boolean mirror = (Boolean) get(cube, "mirror");
            sb.append("{\"uv\":[").append(num(get(uv, "u"))).append(",").append(num(get(uv, "v"))).append("]");
            sb.append(",\"o\":[").append(num(get(origin, "x"))).append(",").append(num(get(origin, "y"))).append(",").append(num(get(origin, "z"))).append("]");
            sb.append(",\"d\":[").append(num(get(dims, "x"))).append(",").append(num(get(dims, "y"))).append(",").append(num(get(dims, "z"))).append("]");
            sb.append(",\"g\":[").append(num(get(grow, "growX"))).append(",").append(num(get(grow, "growY"))).append(",").append(num(get(grow, "growZ"))).append("]");
            sb.append(",\"m\":").append(mirror);
            Object texScale = get(cube, "texScale");
            if (texScale != null) {
                String su = num(get(texScale, "u")), sv = num(get(texScale, "v"));
                if (!su.equals("1") || !sv.equals("1")) sb.append(",\"ts\":[").append(su).append(",").append(sv).append("]");
            }
            // which faces are drawn (the 26.x cube can leave faces out)
            Object visible = get(cube, "visibleFaces");
            if (visible != null) sb.append(",\"vf\":\"").append(visible.toString().replace("\"", "")).append("\"");
            sb.append("}");
        }
        sb.append("],\"children\":[");
        Map<?, ?> children = (Map<?, ?>) get(part, "children");
        first = true;
        for (Map.Entry<?, ?> e : children.entrySet()) {
            if (!first) sb.append(",");
            first = false;
            dumpPart((String) e.getKey(), e.getValue(), depth + 1);
        }
        sb.append("]}");
    }

    public static void main(String[] args) throws Exception {
        String jar = args[0];
        String filter = args.length > 2 ? args[2] : "";
        List<String> classes = new ArrayList<>();
        try (ZipFile z = new ZipFile(jar)) {
            for (Enumeration<? extends ZipEntry> en = z.entries(); en.hasMoreElements(); ) {
                String n = en.nextElement().getName();
                if (n.startsWith("net/minecraft/client/model/") && n.endsWith(".class") && !n.contains("$")) classes.add(n.replace('/', '.').replace(".class", ""));
            }
        }
        Collections.sort(classes);
        sb.append("{");
        boolean firstModel = true;
        int errors = 0;
        for (String cn : classes) {
            if (!filter.isEmpty() && !cn.contains(filter)) continue;
            Class<?> c;
            try { c = Class.forName(cn, false, ModelDump.class.getClassLoader()); } catch (Throwable t) { continue; }
            for (Method m : c.getDeclaredMethods()) {
                if (!Modifier.isStatic(m.getModifiers()) || !m.getReturnType().getSimpleName().equals("LayerDefinition")) continue;
                Object[] a = new Object[m.getParameterCount()];
                boolean ok = true;
                Class<?>[] pt = m.getParameterTypes();
                for (int i = 0; i < pt.length; i++) {
                    if (pt[i] == boolean.class) a[i] = false;
                    else if (pt[i].getSimpleName().equals("CubeDeformation")) {
                        Class<?> cd = pt[i];
                        a[i] = cd.getConstructor(float.class).newInstance(0f);
                    } else if (pt[i] == float.class) a[i] = 0f;
                    else if (pt[i] == int.class) a[i] = 0;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                try {
                    m.setAccessible(true);
                    Object layer = m.invoke(null, a);
                    Object mesh = field(layer, "net.minecraft.client.model.geom.builders.MeshDefinition");
                    if (mesh == null) mesh = get(layer, "mesh");
                    Object root = mesh.getClass().getMethod("getRoot").invoke(mesh);
                    Object tw = get(layer, "texWidth"), th = get(layer, "texHeight");
                    if (!firstModel) sb.append(",");
                    firstModel = false;
                    sb.append("\"").append(cn.substring(cn.lastIndexOf('.') + 1)).append(".").append(m.getName());
                    if (pt.length > 0) sb.append("(").append(pt.length).append(")");
                    sb.append("\":{\"tex\":[").append(tw == null ? "0" : tw).append(",").append(th == null ? "0" : th).append("],\"root\":");
                    dumpPart("root", root, 0);
                    sb.append("}");
                } catch (Throwable t) {
                    errors++;
                    System.err.println("FAILED " + cn + "." + m.getName() + ": " + t);
                    if (t.getCause() != null) System.err.println("   cause: " + t.getCause());
                }
            }
        }
        sb.append("}");
        try (Writer w = new OutputStreamWriter(new FileOutputStream(args[1]), "UTF-8")) { w.write(sb.toString()); }
        System.out.println("done, " + errors + " failures");
    }
}
