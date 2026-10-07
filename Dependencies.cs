using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Unimportable;

internal static class Dependencies
{
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static bool initialized;

    internal static void Initialize()
    {
        if (initialized)
        {
            return;
        }

        initialized = true;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        var assembly = typeof(Dependencies).Assembly;
        if (name == null)
        {
            return null;
        }

        lock (Loaded)
        {
            if (Loaded.TryGetValue(name, out var loaded))
            {
                return loaded;
            }

            using var resource = assembly.GetManifestResourceStream("Unimportable.Dependencies." + name + ".dll");
            if (resource == null)
            {
                return null;
            }

            using var content = new MemoryStream();
            resource.CopyTo(content);
            loaded = Assembly.Load(content.ToArray());
            Loaded.Add(name, loaded);
            return loaded;
        }
    }
}
