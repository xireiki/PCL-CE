using System.IO.Compression;
using System.Text;

namespace PCL;

public static partial class JarInJar
{
    private static bool _CollectFabricNestedJars(ZipArchive jar, List<NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("fabric.mod.json");
            if (entry is null)
                return true;

            var obj = (JsonObject)LauncherCommon.GetJson(LauncherCommon.ReadFile(entry.Open()));
            if (!obj.TryGetPropertyValue("jars", out var jars) || jars is not JsonArray arr)
                return true;

            foreach (var j in arr)
                if (j is JsonObject jo
                    && jo.TryGetPropertyValue("file", out var file)
                    && file is not null)
                    found.Add(
                        new NestedJarInfo
                        {
                            Path = file.ToString()
                        });

            return true;
        }
        catch (Exception ex)
        {
            // 清单可能是作者手写的非法 JSON（例如字符串里存在未转义的换行），
            // 此时回退为扫描标准目录，避免该 Mod 永久停留在“解析未完成”状态。
            LauncherCommon.Log(ex, "解析 fabric.mod.json 内嵌清单失败，回退为目录扫描", LauncherLogLevel.Developer);
            _CollectJarsFromFolder(jar, found, "META-INF/jars/");
            return true;
        }
    }

    /// <summary>清单解析失败时的兜底：按目录约定收集内嵌 jar。</summary>
    private static void _CollectJarsFromFolder(ZipArchive jar, List<NestedJarInfo> found, string prefix)
    {
        foreach (var entry in jar.Entries)
            if (entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && entry.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                found.Add(new NestedJarInfo { Path = entry.FullName });
    }

    private static bool _CollectForgeNestedJars(ZipArchive jar, List<NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("META-INF/jarjar/metadata.json");
            if (entry is null) return true;
            var obj = (JsonObject)LauncherCommon.GetJson(LauncherCommon.ReadFile(entry.Open()));
            if (!obj.TryGetPropertyValue("jars", out var jars) || jars is not JsonArray arr)
                return true;

            foreach (var j in arr)
                if (j is JsonObject jo
                    && jo.TryGetPropertyValue("path", out var p)
                    && p is not null)
                {
                    var identifier = jo["identifier"] as JsonObject;
                    var version = jo["version"] as JsonObject;
                    var group = identifier?["group"]?.ToString();
                    var artifact = identifier?["artifact"]?.ToString();
                    found.Add(
                        new NestedJarInfo
                        {
                            Path = p.ToString(),
                            Identifier =
                                string.IsNullOrWhiteSpace(group)
                                || string.IsNullOrWhiteSpace(artifact)
                                    ? null
                                    : group + ":" + artifact,
                            VersionRange = version?["range"]?.ToString(),
                            ArtifactVersion = version?["artifactVersion"]?.ToString()
                        });
                }

            return true;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "解析 META-INF/jarjar/metadata.json 内嵌清单失败，回退为目录扫描",
                LauncherLogLevel.Developer);
            _CollectJarsFromFolder(jar, found, "META-INF/jarjar/");
            return true;
        }
    }

    private static bool _CollectQuiltNestedJars(ZipArchive jar, List<NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("quilt.mod.json");
            if (entry is null)
                return true;

            var obj = (JsonObject)LauncherCommon.GetJson(LauncherCommon.ReadFile(entry.Open()));
            if (!obj.TryGetPropertyValue("quilt_loader", out var ql)
                || ql is not JsonObject qlo
                || !qlo.TryGetPropertyValue("jars", out var jars)
                || jars is not JsonArray arr)
                return true;

            found.AddRange(
                from j in arr
                select j?.ToString()
                into s
                where !string.IsNullOrEmpty(s)
                select new NestedJarInfo
                {
                    Path = s
                });

            return true;
        }
        catch (Exception ex)
        {
            LauncherCommon.Log(
                ex,
                "解析 quilt.mod.json 内嵌清单失败，回退为目录扫描",
                LauncherLogLevel.Developer);
            _CollectJarsFromFolder(jar, found, "META-INF/jars/");
            return true;
        }
    }

    private static bool _CollectManifestEmbeddedJars(ZipArchive jar, List<NestedJarInfo> found)
    {
        try
        {
            var entry = jar.GetEntry("META-INF/MANIFEST.MF");
            if (entry is null)
                return true;

            var sb = new StringBuilder();
            foreach (var raw in LauncherCommon.ReadFile(entry.Open()).Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith(' '))
                    sb.Append(line, 1, line.Length - 1);
                else
                    sb.Append('\n').Append(line);
            }

            foreach (var line in sb.ToString().Split('\n'))
            {
                if (!line.StartsWith(
                        "Embedded-Dependencies-Mod:",
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = line["Embedded-Dependencies-Mod:".Length..].Trim();
                if (!string.IsNullOrEmpty(value))
                    found.Add(
                        new NestedJarInfo
                        {
                            Path = value
                        });

                return true;
            }

            return true;
        }
        catch (Exception ex)
        {
            // 清单本身读不出来时无法再推断路径，只能放弃内嵌信息，但不能因此让整个 Mod 永久处于未解析状态。
            LauncherCommon.Log(ex, "解析 MANIFEST.MF 内嵌声明失败", LauncherLogLevel.Developer);
            return true;
        }
    }

    /// <summary>按存在的清单文件判断 jar 声明的加载器（Fabric/Quilt/Forge/NeoForge）；无从判断返回 null。</summary>
    public static string? DetectLoader(ZipArchive jar, string? preferredLoader = null)
    {
        var hasFabric = jar.GetEntry("fabric.mod.json") is not null;
        var hasQuilt = jar.GetEntry("quilt.mod.json") is not null;
        var hasNeoForge = jar.GetEntry("META-INF/neoforge.mods.toml") is not null;
        var hasForge = jar.GetEntry("META-INF/mods.toml") is not null
            || jar.GetEntry("mcmod.info") is not null;

        var preferred = preferredLoader?.ToUpperInvariant() switch
        {
            "NEOFORGE" when hasNeoForge => "NeoForge",
            "FORGE" when hasForge => "Forge",
            "FABRIC" when hasFabric => "Fabric",
            "QUILT" when hasQuilt => "Quilt",
            _ => null
        };

        return preferred
            ?? (hasFabric ? "Fabric"
                : hasQuilt ? "Quilt"
                : hasNeoForge ? "NeoForge"
                : hasForge ? "Forge"
                : null);
    }
}