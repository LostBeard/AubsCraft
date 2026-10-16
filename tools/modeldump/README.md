# modeldump - the game's own creature models

`ModelDumpObf.java` calls the `createBodyLayer()` of every model class of a Minecraft client jar and prints the part tree (poses, cubes) as JSON, using Mojang's published mapping file
(`client.txt`, linked from the version's json under `downloads.client_mappings`) to find classes and members by their real names. `filter_models.py` keeps the creature models and writes
`AubsCraft.Live.Model/Entities/mc-models.json` (an embedded resource: `GameModels` reads it, `EntityModels.FromGame.cs` says which creature wears which model).

Re-run for another Minecraft version:

1. an UNSIGNED copy of the client jar (drop `META-INF/*.SF|RSA|DSA|MANIFEST.MF`: the obfuscated classes of one package must not have two signers);
2. the version's libraries as the class path (from the version json: authlib, DFU, joml, fastutil, guava... of THAT version; a newer authlib fails class initialisation);
3. `javac -cp <jar>;<libs> ModelDumpObf.java`, then
   `java -cp <unsigned jar>;<libs>;. ModelDumpObf <unsigned jar> client.txt all.json`
4. `python filter_models.py all.json ../../AubsCraft.Live.Model/Entities/mc-models.json`

Java 21+ (the game's own runtime for 1.21). Needs no game assets and starts no game: a model class only builds its definition. Version 26.x and later are unobfuscated: `ModelDump.java` is the same walk without mappings.
