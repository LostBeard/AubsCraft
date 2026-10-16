import java.io.*;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import java.util.zip.*;

/**
 * Dumps the geometry of every model class of an OBFUSCATED Minecraft client jar (1.21.x), using Mojang's published mapping file to find the classes and members by their real names.
 * Same output as ModelDump. usage: java -cp <jar>;<libs>;. ModelDumpObf <jar> <client.txt> <out.json> [ClassNameFilter]
 */
public class ModelDumpObf {
    static class CM {
        String mapped, obf;
        Map<String, String> fields = new HashMap<>();
        Map<String, List<String[]>> methods = new HashMap<>();   // mapped name -> [obf name, mapped return type, mapped params...]
    }
    static Map<String, CM> byMapped = new HashMap<>();
    static Map<String, CM> byObf = new HashMap<>();
    static StringBuilder sb = new StringBuilder();

    static void loadMapping(String path) throws IOException {
        CM cur = null;
        for (String line : Files.readAllLines(Paths.get(path), StandardCharsets.UTF_8)) {
            if (line.startsWith("#") || line.isEmpty()) continue;
            if (!line.startsWith(" ")) {
                String[] p = line.substring(0, line.length() - 1).split(" -> ");
                cur = new CM();
                cur.mapped = p[0];
                cur.obf = p[1];
                byMapped.put(cur.mapped, cur);
                byObf.put(cur.obf, cur);
            } else if (cur != null) {
                String l = line.trim();
                String[] ar = l.split(" -> ");
                if (ar.length != 2) continue;
                String left = ar[0], obf = ar[1];
                int paren = left.indexOf('(');
                if (paren < 0) {
                    String[] tn = left.split(" ");
                    cur.fields.put(tn[tn.length - 1], obf);
                } else {
                    // optional "12:12:" prefix
                    String sig = left.replaceFirst("^\\d+:\\d+:", "");
                    String ret = sig.substring(0, sig.indexOf(' '));
                    String name = sig.substring(sig.indexOf(' ') + 1, sig.indexOf('('));
                    String params = sig.substring(sig.indexOf('(') + 1, sig.lastIndexOf(')'));
                    List<String> entry = new ArrayList<>();
                    entry.add(obf); entry.add(ret);
                    if (!params.isEmpty()) entry.addAll(Arrays.asList(params.split(",")));
                    cur.methods.computeIfAbsent(name, k -> new ArrayList<>()).add(entry.toArray(new String[0]));
                }
            }
        }
    }

    static Object get(Object o, String mappedClass, String mappedField) throws Exception {
        CM cm = byMapped.get(mappedClass);
        String name = cm != null && cm.fields.containsKey(mappedField) ? cm.fields.get(mappedField) : mappedField;
        for (Class<?> c = o.getClass(); c != null; c = c.getSuperclass()) {
            try { Field f = c.getDeclaredField(name); f.setAccessible(true); return f.get(o); } catch (NoSuchFieldException e) { }
        }
        throw new NoSuchFieldException(mappedClass + "." + mappedField + " (" + name + ")");
    }

    static String num(Object o) {
        double d = ((Number) o).doubleValue();
        if (d == Math.rint(d)) return String.valueOf((long) d);
        return String.valueOf((float) d);
    }

    static final String PD = "net.minecraft.client.model.geom.builders.PartDefinition";
    static final String CD = "net.minecraft.client.model.geom.builders.CubeDefinition";
    static final String PP = "net.minecraft.client.model.geom.PartPose";
    static final String CDF = "net.minecraft.client.model.geom.builders.CubeDeformation";
    static final String UV = "net.minecraft.client.model.geom.builders.UVPair";
    static final String LD = "net.minecraft.client.model.geom.builders.LayerDefinition";
    static final String MD = "net.minecraft.client.model.geom.builders.MeshDefinition";

    static void dumpPart(String name, Object part) throws Exception {
        sb.append("{\"name\":\"").append(name).append("\"");
        Object pose = get(part, PD, "partPose");
        sb.append(",\"pose\":[");
        String[] n = {"x", "y", "z", "xRot", "yRot", "zRot"};
        for (int i = 0; i < n.length; i++) sb.append(i > 0 ? "," : "").append(num(get(pose, PP, n[i])));
        sb.append("]");
        try {
            Object a = get(pose, PP, "xScale"), b = get(pose, PP, "yScale"), c = get(pose, PP, "zScale");
            if (!num(a).equals("1") || !num(b).equals("1") || !num(c).equals("1")) sb.append(",\"scale\":[").append(num(a)).append(",").append(num(b)).append(",").append(num(c)).append("]");
        } catch (NoSuchFieldException e) { }
        sb.append(",\"cubes\":[");
        List<?> cubes = (List<?>) get(part, PD, "cubes");
        boolean first = true;
        for (Object cube : cubes) {
            if (!first) sb.append(",");
            first = false;
            Object origin = get(cube, CD, "origin"), dims = get(cube, CD, "dimensions"), grow = get(cube, CD, "grow"), uv = get(cube, CD, "texCoord");
            boolean mirror = (Boolean) get(cube, CD, "mirror");
            sb.append("{\"uv\":[").append(num(get(uv, UV, "u"))).append(",").append(num(get(uv, UV, "v"))).append("]");
            sb.append(",\"o\":[").append(num(get(origin, "org.joml.Vector3f", "x"))).append(",").append(num(get(origin, "org.joml.Vector3f", "y"))).append(",").append(num(get(origin, "org.joml.Vector3f", "z"))).append("]");
            sb.append(",\"d\":[").append(num(get(dims, "org.joml.Vector3f", "x"))).append(",").append(num(get(dims, "org.joml.Vector3f", "y"))).append(",").append(num(get(dims, "org.joml.Vector3f", "z"))).append("]");
            sb.append(",\"g\":[").append(num(get(grow, CDF, "growX"))).append(",").append(num(get(grow, CDF, "growY"))).append(",").append(num(get(grow, CDF, "growZ"))).append("]");
            sb.append(",\"m\":").append(mirror);
            try {
                Object ts = get(cube, CD, "texScale");
                if (ts != null) {
                    String su = num(get(ts, UV, "u")), sv = num(get(ts, UV, "v"));
                    if (!su.equals("1") || !sv.equals("1")) sb.append(",\"ts\":[").append(su).append(",").append(sv).append("]");
                }
            } catch (NoSuchFieldException e) { }
            sb.append("}");
        }
        sb.append("],\"children\":[");
        Map<?, ?> children = (Map<?, ?>) get(part, PD, "children");
        first = true;
        for (Map.Entry<?, ?> e : children.entrySet()) {
            if (!first) sb.append(",");
            first = false;
            dumpPart((String) e.getKey(), e.getValue());
        }
        sb.append("]}");
    }

    static String mappedTypeName(Class<?> c) {
        if (c.isPrimitive()) return c.getName();
        CM cm = byObf.get(c.getName());
        return cm != null ? cm.mapped : c.getName();
    }

    public static void main(String[] args) throws Exception {
        String jar = args[0];
        loadMapping(args[1]);
        String filter = args.length > 3 ? args[3] : "";
        ClassLoader cl = ModelDumpObf.class.getClassLoader();
        Class<?> layerClass = Class.forName(byMapped.get(LD).obf, false, cl);
        Class<?> cubeDeformation = Class.forName(byMapped.get(CDF).obf, false, cl);
        Class<?> meshClass = Class.forName(byMapped.get(MD).obf, false, cl);
        List<String> mappedClasses = new ArrayList<>();
        for (String m : byMapped.keySet())
            if (m.startsWith("net.minecraft.client.model.") && !m.contains("$") && !m.startsWith("net.minecraft.client.model.geom") ) mappedClasses.add(m);
        Collections.sort(mappedClasses);
        sb.append("{");
        boolean firstModel = true;
        int errors = 0;
        for (String mc : mappedClasses) {
            if (!filter.isEmpty() && !mc.contains(filter)) continue;
            Class<?> c;
            try { c = Class.forName(byMapped.get(mc).obf, false, cl); } catch (Throwable t) { continue; }
            Method[] methods;
            try { methods = c.getDeclaredMethods(); } catch (Throwable t) { continue; }
            for (Method m : methods) {
                if (!Modifier.isStatic(m.getModifiers()) || m.getReturnType() != layerClass) continue;
                Class<?>[] pt = m.getParameterTypes();
                Object[] a = new Object[pt.length];
                boolean ok = true;
                for (int i = 0; i < pt.length; i++) {
                    if (pt[i] == boolean.class) a[i] = false;
                    else if (pt[i] == cubeDeformation) a[i] = cubeDeformation.getConstructor(float.class).newInstance(0f);
                    else if (pt[i] == float.class) a[i] = 0f;
                    else if (pt[i] == int.class) a[i] = 0;
                    else { ok = false; break; }
                }
                if (!ok) continue;
                // The method's mapped name: the mapped entry of this class with this obf name and the same parameter types
                String label = m.getName();
                CM cmc = byMapped.get(mc);
                outer:
                for (Map.Entry<String, List<String[]>> e : cmc.methods.entrySet())
                    for (String[] entry : e.getValue()) {
                        if (!entry[0].equals(m.getName()) || entry.length - 2 != pt.length) continue;
                        boolean same = true;
                        for (int i = 0; i < pt.length; i++) if (!entry[2 + i].equals(mappedTypeName(pt[i]))) { same = false; break; }
                        if (same) { label = e.getKey(); break outer; }
                    }
                try {
                    m.setAccessible(true);
                    Object layer = m.invoke(null, a);
                    Object mesh = get(layer, LD, "mesh");
                    Object root = get(mesh, MD, "root");
                    Object material = get(layer, LD, "material");
                    String tw = "0", th = "0";
                    try {
                        // MaterialDefinition: width, height
                        for (Field f : material.getClass().getDeclaredFields()) {
                            if (f.getType() == int.class) { f.setAccessible(true); if (tw.equals("0")) tw = String.valueOf(f.get(material)); else if (th.equals("0")) th = String.valueOf(f.get(material)); }
                        }
                    } catch (Throwable t) { }
                    if (!firstModel) sb.append(",");
                    firstModel = false;
                    sb.append("\"").append(mc.substring(mc.lastIndexOf('.') + 1)).append(".").append(label);
                    if (pt.length > 0) sb.append("(").append(pt.length).append(")");
                    sb.append("\":{\"tex\":[").append(tw).append(",").append(th).append("],\"root\":");
                    dumpPart("root", root);
                    sb.append("}");
                } catch (Throwable t) {
                    errors++;
                    System.err.println("FAILED " + mc + "." + label + ": " + t + (t.getCause() != null ? " / " + t.getCause() : ""));
                }
            }
        }
        sb.append("}");
        try (Writer w = new OutputStreamWriter(new FileOutputStream(args[2]), "UTF-8")) { w.write(sb.toString()); }
        System.out.println("done, " + errors + " failures");
    }
}
