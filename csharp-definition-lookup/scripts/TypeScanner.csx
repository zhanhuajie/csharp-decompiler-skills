using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

if (Args.Count < 1)
{
    Console.Error.WriteLine("Usage: TypeScanner <SymbolName> [NamespaceFilter]");
    Environment.Exit(1);
}
string targetType = Args[0];
string namespaceFilter = Args.Count > 1 ? Args[1] : null;

string line;
while ((line = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    string dllPath = line.Trim();
    if (File.Exists(dllPath))
    {
        try
        {
            using var fs = new FileStream(dllPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var peReader = new PEReader(fs);
            if (!peReader.HasMetadata) continue;
            
            var mdReader = peReader.GetMetadataReader();
            foreach (var typeDefHandle in mdReader.TypeDefinitions)
            {
                var typeDef = mdReader.GetTypeDefinition(typeDefHandle);
                string name = mdReader.GetString(typeDef.Name);
                if (name == targetType)
                {
                    string ns = mdReader.GetString(typeDef.Namespace);
                    
                    if (!string.IsNullOrEmpty(namespaceFilter) && !ns.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    
                    string fullName = string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                    Console.WriteLine($"{fullName}|{dllPath}");
                }
            }
        }
        catch
        {
            // Safely ignore unreadable or invalid assemblies
        }
    }
}