using System.IO.Compression;
using System.Formats.Tar;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

public class Program
{
    public static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: SdkLayoutAnalyzer <path-to-sdk-zip-or-tar> [subdirectory-relative-to-sdk-root] [--scan=assemblies|non-assemblies] [--remove-duplicates]");
            Console.WriteLine("  --scan=assemblies (default): Scan .dll and .exe files");
            Console.WriteLine("  --scan=non-assemblies: Scan all files except .dll and .exe");
            Console.WriteLine("  --remove-duplicates: Remove duplicate files and re-create archive with size comparison");
            return;
        }

        // Parse command line arguments
        string sdkPath = args[0];
        ScanType scanType = ScanType.Assemblies; // Default
        bool removeDuplicates = false;

        // Look for options
        foreach (var arg in args)
        {
            if (arg.StartsWith("--scan=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg.Substring("--scan=".Length);
                if (value.Equals("assemblies", StringComparison.OrdinalIgnoreCase))
                {
                    scanType = ScanType.Assemblies;
                }
                else if (value.Equals("non-assemblies", StringComparison.OrdinalIgnoreCase))
                {
                    scanType = ScanType.NonAssemblies;
                }
                else
                {
                    Console.WriteLine($"Invalid scan type: {value}. Use 'assemblies' or 'non-assemblies'.");
                    return;
                }
            }
            else if (arg.Equals("--remove-duplicates", StringComparison.OrdinalIgnoreCase))
            {
                removeDuplicates = true;
            }
        }

        bool isArchive = sdkPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || sdkPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
        string? originalArchivePath = isArchive ? sdkPath : null;
        long originalArchiveSize = 0;
        long newArchiveSize = 0;
        string? tempDir = null;

        if (isArchive)
        {
            originalArchiveSize = new FileInfo(sdkPath).Length;
        }

        try
        {
            if (isArchive)
            {
                tempDir = Path.Combine(Path.GetTempPath(), "SdkLayoutAnalyzer_" + Guid.NewGuid());
                Directory.CreateDirectory(tempDir);
                ExtractArchive(sdkPath, tempDir);
                sdkPath = tempDir;
            }

            // Determine subdirectory to analyze (default to 'sdk' subfolder)
            string subDir = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "sdk";
            string analyzeDir = Path.Combine(sdkPath, subDir);
            if (!Directory.Exists(analyzeDir))
            {
                Console.WriteLine($"Subdirectory '{subDir}' does not exist in extracted SDK.");
                return;
            }

            var results = Analyze(analyzeDir, scanType).ToList();

            var duplicateGroups = GetDuplicateGroups(results);
            PrintDuplicates(duplicateGroups, sdkPath);

            // Remove duplicates if requested
            if (removeDuplicates && duplicateGroups.Count > 0)
            {
                if (!isArchive)
                {
                    Console.WriteLine("Error: --remove-duplicates can only be used with archive files (.zip or .tar.gz)");
                    return;
                }

                Console.WriteLine();
                Console.WriteLine("Removing duplicate files...");
                RemoveDuplicateFiles(duplicateGroups);

                // Re-create the archive
                string newArchivePath = CreateNewArchivePath(originalArchivePath!);
                CreateArchive(tempDir!, newArchivePath, originalArchivePath!);
                newArchiveSize = new FileInfo(newArchivePath).Length;
            }

            PrintSummary(duplicateGroups, originalArchiveSize, newArchiveSize);
        }
        finally
        {
            if (tempDir != null && Directory.Exists(tempDir))
            {
               Directory.Delete(tempDir, true);
            }
        }
    }

    private static IEnumerable<SdkFileInfo> Analyze(string rootDirectory, ScanType scanType)
    {
        var files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            bool isAssembly = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

            // Filter based on scan type
            if (scanType == ScanType.Assemblies && !isAssembly)
            {
                continue;
            }
            else if (scanType == ScanType.NonAssemblies && isAssembly)
            {
                continue;
            }

            var info = new SdkFileInfo
            {
                Filename = Path.GetFileName(file),
                FilePath = file,
                FileSize = new FileInfo(file).Length,
                FileHash = GetFileHash(file)
            };

            // Try to get target framework from assembly attribute if .dll or .exe
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var assembly = System.Reflection.AssemblyName.GetAssemblyName(file);
                    info = info with { AssemblyVersion = assembly.Version?.ToString(), Culture = string.IsNullOrEmpty(assembly.CultureName) ? "neutral" : assembly.CultureName };

                    // Try to get TargetFrameworkAttribute from assembly metadata using reflection-only loading
                    info = info with { TargetFramework = GetTargetFrameworkFromAssembly(file) };
                }
                catch { }
            }

            // Try to get file version info (applies to many file types)
            try
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(file);
                info = info with { FileVersion = versionInfo.FileVersion };
            }
            catch { }

            yield return info;
        }
    }

    private static string GetFileHash(string filePath)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexStringLower(hash);
    }

    private static string? GetTargetFrameworkFromAssembly(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var peReader = new PEReader(stream);

            if (!peReader.HasMetadata)
                return null;

            var metadataReader = peReader.GetMetadataReader();

            // First, try to find TargetFrameworkAttribute
            foreach (var customAttrHandle in metadataReader.CustomAttributes)
            {
                var customAttr = metadataReader.GetCustomAttribute(customAttrHandle);

                if (customAttr.Constructor.Kind == HandleKind.MethodDefinition)
                {
                    var methodDef = metadataReader.GetMethodDefinition((MethodDefinitionHandle)customAttr.Constructor);
                    var typeDef = metadataReader.GetTypeDefinition(methodDef.GetDeclaringType());
                    var typeName = metadataReader.GetString(typeDef.Name);
                    var namespaceName = metadataReader.GetString(typeDef.Namespace);

                    if (namespaceName == "System.Runtime.Versioning" && typeName == "TargetFrameworkAttribute")
                    {
                        var value = customAttr.DecodeValue(new CustomAttributeTypeProvider());
                        if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string frameworkName)
                        {
                            return frameworkName;
                        }
                    }
                }
                else if (customAttr.Constructor.Kind == HandleKind.MemberReference)
                {
                    var memberRef = metadataReader.GetMemberReference((MemberReferenceHandle)customAttr.Constructor);
                    if (memberRef.Parent.Kind == HandleKind.TypeReference)
                    {
                        var typeRef = metadataReader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                        var typeName = metadataReader.GetString(typeRef.Name);
                        var namespaceName = metadataReader.GetString(typeRef.Namespace);

                        if (namespaceName == "System.Runtime.Versioning" && typeName == "TargetFrameworkAttribute")
                        {
                            var value = customAttr.DecodeValue(new CustomAttributeTypeProvider());
                            if (value.FixedArguments.Length > 0 && value.FixedArguments[0].Value is string frameworkName)
                            {
                                return frameworkName;
                            }
                        }
                    }
                }
            }

            // If TargetFrameworkAttribute not found (common for resource assemblies),
            // try to infer from assembly references
            return InferTargetFrameworkFromReferences(metadataReader);
        }
        catch
        {
            // Ignore errors and return null
        }
        return null;
    }

    private static string? InferTargetFrameworkFromReferences(MetadataReader metadataReader)
    {
        try
        {
            foreach (var assemblyRefHandle in metadataReader.AssemblyReferences)
            {
                var assemblyRef = metadataReader.GetAssemblyReference(assemblyRefHandle);
                var name = metadataReader.GetString(assemblyRef.Name);
                var version = assemblyRef.Version;

                // Handle netstandard specifically
                if (name == "netstandard")
                {
                    if (version.Major == 1)
                        return $".NETStandard,Version=v1.{version.Minor}";
                    else if (version.Major == 2)
                        return $".NETStandard,Version=v2.{version.Minor}";
                }

                // System.Runtime and mscorlib version mapping to frameworks
                if (name == "System.Runtime" || name == "mscorlib")
                {
                    // Common version mappings
                    if (version.Major == 4)
                    {
                        if (version.Minor == 0 && version.Build == 0)
                            return ".NETFramework,Version=v4.5";
                        else if (version.Minor == 1 && version.Build == 0)
                            return ".NETFramework,Version=v4.6";
                        else if (version.Minor == 2)
                        {
                            if (version.Build == 0)
                                return ".NETCoreApp,Version=v2.0";
                            else if (version.Build == 1)
                                return ".NETCoreApp,Version=v2.1";
                            else if (version.Build == 2)
                                return ".NETCoreApp,Version=v3.1";
                        }
                    }
                    else if (version.Major == 5 && version.Minor == 0)
                        return ".NETCoreApp,Version=v5.0";
                    else if (version.Major == 6 && version.Minor == 0)
                        return ".NETCoreApp,Version=v6.0";
                    else if (version.Major == 7 && version.Minor == 0)
                        return ".NETCoreApp,Version=v7.0";
                    else if (version.Major == 8 && version.Minor == 0)
                        return ".NETCoreApp,Version=v8.0";
                    else if (version.Major == 9 && version.Minor == 0)
                        return ".NETCoreApp,Version=v9.0";
                    else if (version.Major == 10 && version.Minor == 0)
                        return ".NETCoreApp,Version=v10.0";
                }
            }
        }
        catch
        {
            // Ignore errors
        }
        return null;
    }

    private static void ExtractArchive(string archivePath, string tempDir)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, tempDir);
        }
        else if (archivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || archivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream fs = File.OpenRead(archivePath);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) != null)
            {
                string outPath = Path.Combine(tempDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(outPath);
                }
                else if (entry.EntryType == TarEntryType.RegularFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                    using var outStream = File.Create(outPath);
                    entry.DataStream?.CopyTo(outStream);
                }
            }
        }
        else
        {
            Console.WriteLine("Unsupported archive format. Please provide a .zip, .tar.gz, or .tgz file.");
            throw new InvalidOperationException();
        }
    }

    private static List<IGrouping<(string? Filename, string? Culture, string? TargetFramework), SdkFileInfo>> GetDuplicateGroups(List<SdkFileInfo> results)
    {
        return results
            .GroupBy(f => (f.Filename, f.Culture, f.TargetFramework))
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.Filename)
            .ThenBy(g => g.Key.Culture)
            .ThenBy(g => g.Key.TargetFramework)
            .ToList();
    }

    private static void PrintDuplicates(List<IGrouping<(string? Filename, string? Culture, string? TargetFramework), SdkFileInfo>> duplicateGroups, string tempDir)
    {
        Console.WriteLine("Filename,Culture,TargetFramework,RelativePath,AssemblyVersion,FileVersion,FileHash,FileSize");
        foreach (var group in duplicateGroups)
        {
            foreach (var info in group.OrderBy(f => f.FilePath))
            {
                string relPath = Path.GetRelativePath(tempDir, info.FilePath ?? "");
                string tfm = EscapeCsv(info.TargetFramework);
                Console.WriteLine($"{info.Filename},{info.Culture},{tfm},{relPath},{info.AssemblyVersion},{info.FileVersion},{info.FileHash},{info.FileSize}");
            }
        }
    }

    /// <summary>
    /// Determines which duplicate file to keep based on:
    /// 1. Lowest version (AssemblyVersion if available, otherwise FileVersion)
    /// 2. If multiple files have the same lowest version, choose the largest file
    /// </summary>
    private static SdkFileInfo GetFileToKeep(IEnumerable<SdkFileInfo> duplicateGroup)
    {
        return duplicateGroup
            .OrderBy(f =>
            {
                // Try to parse AssemblyVersion first, then FileVersion
                string? versionStr = f.AssemblyVersion ?? f.FileVersion;
                if (string.IsNullOrEmpty(versionStr))
                    return new Version(int.MaxValue, 0, 0, 0); // No version, sort to end

                // Try to parse as Version
                if (Version.TryParse(versionStr, out var version))
                    return version;

                // If parsing fails, sort to end
                return new Version(int.MaxValue, 0, 0, 0);
            })
            .ThenByDescending(f => f.FileSize) // If same version, prefer largest file
            .First();
    }

    /// <summary>
    /// Removes duplicate files from the file system, keeping only the file chosen by GetFileToKeep
    /// </summary>
    private static void RemoveDuplicateFiles(List<IGrouping<(string? Filename, string? Culture, string? TargetFramework), SdkFileInfo>> duplicateGroups)
    {
        int filesRemoved = 0;
        foreach (var group in duplicateGroups)
        {
            var fileToKeep = GetFileToKeep(group);
            var filesToRemove = group.Where(f => f != fileToKeep);

            foreach (var file in filesToRemove)
            {
                if (file.FilePath != null && File.Exists(file.FilePath))
                {
                    File.Delete(file.FilePath);
                    filesRemoved++;
                }
            }
        }
        Console.WriteLine($"Removed {filesRemoved} duplicate files.");
    }

    /// <summary>
    /// Creates a new archive file path based on the original, adding "-deduplicated" before the extension
    /// </summary>
    private static string CreateNewArchivePath(string originalPath)
    {
        string directory = Path.GetDirectoryName(originalPath) ?? "";
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
        string extension = Path.GetExtension(originalPath);

        // Handle .tar.gz specially
        if (originalPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            fileNameWithoutExt = Path.GetFileNameWithoutExtension(fileNameWithoutExt); // Remove .tar
            return Path.Combine(directory, $"{fileNameWithoutExt}-deduplicated.tar.gz");
        }

        return Path.Combine(directory, $"{fileNameWithoutExt}-deduplicated{extension}");
    }

    /// <summary>
    /// Creates a new archive from the directory, using the same format as the original
    /// </summary>
    private static void CreateArchive(string sourceDir, string outputPath, string originalArchivePath)
    {
        if (originalArchivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.CreateFromDirectory(sourceDir, outputPath, CompressionLevel.Optimal, false);
        }
        else if (originalArchivePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                 originalArchivePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            using FileStream fs = File.Create(outputPath);
            using var gzip = new GZipStream(fs, CompressionLevel.Optimal);
            using var tar = new TarWriter(gzip);

            // Add all files and directories
            AddDirectoryToTar(tar, sourceDir, "");
        }
    }

    /// <summary>
    /// Recursively adds directory contents to a tar archive
    /// </summary>
    private static void AddDirectoryToTar(TarWriter tar, string sourceDir, string relativePath)
    {
        foreach (var entry in Directory.GetFileSystemEntries(sourceDir))
        {
            string entryName = Path.GetFileName(entry);
            string entryRelativePath = string.IsNullOrEmpty(relativePath) ? entryName : $"{relativePath}/{entryName}";

            if (Directory.Exists(entry))
            {
                // Recursively add contents (directories will be created implicitly)
                AddDirectoryToTar(tar, entry, entryRelativePath);
            }
            else if (File.Exists(entry))
            {
                // Add file entry - note: parameters are (fileName, entryName)
                tar.WriteEntry(entry, entryRelativePath);
            }
        }
    }

    private static void PrintSummary(List<IGrouping<(string? Filename, string? Culture, string? TargetFramework), SdkFileInfo>> duplicateGroups, long originalArchiveSize = 0, long newArchiveSize = 0)
    {
        int totalDuplicatedFiles = duplicateGroups.Sum(g => g.Count() - 1);

        // Calculate total duplicated content by choosing which file to keep and summing the other files' actual sizes
        long totalDuplicatedContent = duplicateGroups.Sum(g =>
        {
            var fileToKeep = GetFileToKeep(g);
            // Sum the actual sizes of all OTHER files (the duplicates that would be removed)
            return g.Where(f => f != fileToKeep).Sum(f => f.FileSize);
        });

        // Calculate duplicate categorization statistics
        int sameHashCount = 0;
        int differentVersionCount = 0;
        int sameVersionDifferentHashCount = 0;

        foreach (var group in duplicateGroups)
        {
            var fileToKeep = GetFileToKeep(group);
            var duplicates = group.Where(f => f != fileToKeep);

            foreach (var duplicate in duplicates)
            {
                // Get versions for comparison
                string? keepVersion = fileToKeep.AssemblyVersion ?? fileToKeep.FileVersion;
                string? dupVersion = duplicate.AssemblyVersion ?? duplicate.FileVersion;

                if (duplicate.FileHash == fileToKeep.FileHash)
                {
                    // Same hash as file to keep
                    sameHashCount++;
                }
                else if (keepVersion != dupVersion)
                {
                    // Different version from file to keep
                    differentVersionCount++;
                }
                else
                {
                    // Same version but different hash
                    sameVersionDifferentHashCount++;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Total duplicated files (extra copies): {totalDuplicatedFiles}");
        Console.WriteLine($"Total duplicated file content (MB): {totalDuplicatedContent / 1024.0 / 1024.0:F1}");
        Console.WriteLine();
        Console.WriteLine("Duplicate categorization (relative to lowest version file to keep):");
        Console.WriteLine($"  Duplicates with same hash as file to keep: {sameHashCount}");
        Console.WriteLine($"  Duplicates with different version: {differentVersionCount}");
        Console.WriteLine($"  Duplicates with same version but different hash: {sameVersionDifferentHashCount}");

        // Verify counts add up correctly
        int categorySum = sameHashCount + differentVersionCount + sameVersionDifferentHashCount;
        if (categorySum != totalDuplicatedFiles)
        {
            Console.WriteLine($"  WARNING: Category counts ({categorySum}) do not match total duplicated files ({totalDuplicatedFiles})");
        }

        var topDuplicated = duplicateGroups
            .Select(g =>
            {
                var fileToKeep = GetFileToKeep(g);
                // Sum the actual sizes of all OTHER files (the duplicates that would be removed)
                long aggregateDuplicatedSize = g.Where(f => f != fileToKeep).Sum(f => f.FileSize);

                return new
                {
                    Filename = g.Key.Filename,
                    Culture = g.Key.Culture,
                    TargetFramework = g.Key.TargetFramework,
                    Count = g.Count(),
                    FileSize = fileToKeep.FileSize,
                    AggregateDuplicatedSize = aggregateDuplicatedSize
                };
            })
            .OrderByDescending(x => x.AggregateDuplicatedSize)
            .ThenByDescending(x => x.FileSize)
            .Take(10)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("Top 10 Largest Duplicated Files:");
        Console.WriteLine("Filename,Culture,TargetFramework,DuplicateCount,DuplicatedSize(MB)");
        foreach (var item in topDuplicated)
        {
            string tfm = EscapeCsv(item.TargetFramework);
            double aggSizeMB = item.AggregateDuplicatedSize / 1024.0 / 1024.0;
            Console.WriteLine($"{item.Filename},{item.Culture},{tfm},{item.Count},{aggSizeMB:F1}");
        }

        // Display archive size comparison if duplicates were removed
        if (originalArchiveSize > 0 && newArchiveSize > 0)
        {
            double originalSizeMB = originalArchiveSize / 1024.0 / 1024.0;
            double newSizeMB = newArchiveSize / 1024.0 / 1024.0;
            double reductionMB = (originalArchiveSize - newArchiveSize) / 1024.0 / 1024.0;
            double reductionPercent = ((double)(originalArchiveSize - newArchiveSize) / originalArchiveSize) * 100.0;

            Console.WriteLine();
            Console.WriteLine("Archive Size Comparison:");
            Console.WriteLine($"  Original archive size (MB): {originalSizeMB:F1}");
            Console.WriteLine($"  New archive size (MB): {newSizeMB:F1}");
            Console.WriteLine($"  Size reduction (MB): {reductionMB:F1}");
            Console.WriteLine($"  Size reduction (%): {reductionPercent:F1}%");
        }
    }

    // Escapes a string for CSV output: wraps in double quotes if it contains comma or quote, and escapes embedded quotes
    private static string EscapeCsv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains('"'))
            value = value.Replace("\"", "\"\"");
        if (value.Contains(',') || value.Contains('"'))
            return $"\"{value}\"";
        return value;
    }
    
    // SdkFileInfo will be a private nested record below
    internal record SdkFileInfo
    {
        public string? Filename { get; init; }
        public string? FilePath { get; init; }
        public string? AssemblyVersion { get; init; }
        public string? FileVersion { get; init; }
        public string? Culture { get; init; }
        public string? FileHash { get; init; }
        public long FileSize { get; init; }
        public string? TargetFramework { get; init; }
    }

    internal class CustomAttributeTypeProvider : ICustomAttributeTypeProvider<object>
    {
        public object GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return typeCode switch
            {
                PrimitiveTypeCode.Boolean => typeof(bool),
                PrimitiveTypeCode.Byte => typeof(byte),
                PrimitiveTypeCode.SByte => typeof(sbyte),
                PrimitiveTypeCode.Char => typeof(char),
                PrimitiveTypeCode.Int16 => typeof(short),
                PrimitiveTypeCode.UInt16 => typeof(ushort),
                PrimitiveTypeCode.Int32 => typeof(int),
                PrimitiveTypeCode.UInt32 => typeof(uint),
                PrimitiveTypeCode.Int64 => typeof(long),
                PrimitiveTypeCode.UInt64 => typeof(ulong),
                PrimitiveTypeCode.Single => typeof(float),
                PrimitiveTypeCode.Double => typeof(double),
                PrimitiveTypeCode.String => typeof(string),
                _ => typeof(object)
            };
        }

        public object GetSystemType() => typeof(Type);

        public object GetSZArrayType(object _) => typeof(Array);

        public object GetTypeFromDefinition(MetadataReader _, TypeDefinitionHandle __, byte ___) => typeof(object);

        public object GetTypeFromReference(MetadataReader _, TypeReferenceHandle __, byte ___) => typeof(object);

        public object GetTypeFromSerializedName(string _) => typeof(object);

        public PrimitiveTypeCode GetUnderlyingEnumType(object _) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(object type) => type.Equals(typeof(Type));
    }

    private enum ScanType
    {
        Assemblies,
        NonAssemblies
    }
}
