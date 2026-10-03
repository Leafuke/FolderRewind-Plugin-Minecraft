namespace MineRewind.Discovery;

internal sealed class MinecraftLayoutScanner(MinecraftDiscoveryContext context)
{
    private readonly Dictionary<string, MinecraftDiscoveredInstance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _worldOwners = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<MinecraftDiscoveredInstance> Instances => _instances.Values
        .OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Scan(MinecraftLocation location)
    {
        context.Check();
        var root = MinecraftDiscoveryContext.Normalize(location.Path);
        if (root is null || !Directory.Exists(root)) return;
        switch (location.Kind)
        {
            case MinecraftLocationKind.WorldDirectory:
                var parent = Directory.GetParent(root)?.FullName;
                var inSaves = string.Equals(System.IO.Path.GetFileName(parent), "saves", StringComparison.OrdinalIgnoreCase);
                var inBedrock = location.Edition == MinecraftEdition.Bedrock && parent is not null;
                var instanceRoot = inSaves ? Directory.GetParent(parent!)?.FullName ?? root : inBedrock ? parent! : root;
                AddWorld(instanceRoot, location.Name ?? InstanceName(instanceRoot), root, location);
                break;
            case MinecraftLocationKind.WorldsDirectory:
                var instance = location.Edition == MinecraftEdition.Java
                    && string.Equals(System.IO.Path.GetFileName(root), "saves", StringComparison.OrdinalIgnoreCase)
                    ? Directory.GetParent(root)?.FullName ?? root : root;
                ScanWorlds(instance, location.Name ?? InstanceName(instance), root, location);
                break;
            case MinecraftLocationKind.JavaGameRoot:
                ScanWorlds(root, location.Name ?? InstanceName(root), System.IO.Path.Combine(root, "saves"), location);
                foreach (var version in context.Directories(System.IO.Path.Combine(root, "versions"), location.Source))
                    ScanWorlds(version, System.IO.Path.GetFileName(version), System.IO.Path.Combine(version, "saves"), location);
                break;
        }
    }

    public void ScanUserRoot(string root)
    {
        context.Check();
        if (File.Exists(System.IO.Path.Combine(root, "level.dat")))
        {
            Scan(new(root, MinecraftLocationKind.WorldDirectory, GuessEdition(root), null, "user"));
            return;
        }
        var name = System.IO.Path.GetFileName(root);
        if (name.Equals("saves", StringComparison.OrdinalIgnoreCase)
            || name.Equals("minecraftWorlds", StringComparison.OrdinalIgnoreCase))
        {
            Scan(new(root, MinecraftLocationKind.WorldsDirectory,
                name.Equals("minecraftWorlds", StringComparison.OrdinalIgnoreCase) ? MinecraftEdition.Bedrock : MinecraftEdition.Java,
                null, "user"));
            return;
        }
        Scan(new(root, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, "user"));
        foreach (var nested in new[] { ".minecraft", "minecraft" })
            Scan(new(System.IO.Path.Combine(root, nested), MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, "user"));
        // Also accept an arbitrarily named world collection, without recursively searching it.
        foreach (var child in context.Directories(root, "user"))
        {
            if (File.Exists(System.IO.Path.Combine(child, "level.dat")))
                AddWorld(root, InstanceName(root), child,
                    new(root, MinecraftLocationKind.WorldsDirectory, GuessEdition(child), null, "user"));
            if (name.Equals("versions", StringComparison.OrdinalIgnoreCase))
                Scan(new(child, MinecraftLocationKind.JavaGameRoot, MinecraftEdition.Java, null, "user"));
        }
    }

    private void ScanWorlds(string instance, string name, string saves, MinecraftLocation location)
    {
        foreach (var world in context.Directories(saves, location.Source)) AddWorld(instance, name, world, location);
    }

    private void AddWorld(string instanceRoot, string name, string world, MinecraftLocation location)
    {
        context.Check();
        if (!File.Exists(System.IO.Path.Combine(world, "level.dat"))) return;
        instanceRoot = MinecraftDiscoveryContext.Normalize(instanceRoot)!;
        if (string.IsNullOrWhiteSpace(name)) name = InstanceName(instanceRoot);
        world = MinecraftDiscoveryContext.Normalize(world)!;
        if (_worldOwners.TryGetValue(world, out var owner))
        {
            var known = _instances[owner];
            known.Sources.Add(location.Source);
            if (location.Source != "user" && !string.IsNullOrWhiteSpace(location.Name)) known.Name = name;
            return;
        }
        // Edition is part of a collection's identity when a manually selected collection is mixed.
        var key = location.Edition + ":" + instanceRoot;
        if (!_instances.TryGetValue(key, out var instance))
        {
            if (_instances.Count >= MinecraftDiscoveryContext.InstanceLimit) throw new MinecraftDiscoveryBudgetException("instances");
            instance = new(instanceRoot, name, location.Edition);
            var mods = System.IO.Path.Combine(instanceRoot, "mods");
            if (location.Edition == MinecraftEdition.Java && Directory.Exists(mods)) instance.ModsPath = mods;
            _instances.Add(key, instance);
        }
        var worldName = System.IO.Path.GetFileName(world);
        if (location.Edition == MinecraftEdition.Bedrock)
        {
            var label = context.ReadText(System.IO.Path.Combine(world, "levelname.txt"), location.Source)?
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(label)) worldName = label;
        }
        instance.Worlds.Add(new(world, worldName));
        instance.Sources.Add(location.Source);
        _worldOwners.Add(world, key);
    }

    private static MinecraftEdition GuessEdition(string world)
        => Directory.Exists(System.IO.Path.Combine(world, "db"))
            || string.Equals(System.IO.Path.GetFileName(Directory.GetParent(world)?.FullName), "minecraftWorlds",
                StringComparison.OrdinalIgnoreCase) ? MinecraftEdition.Bedrock : MinecraftEdition.Java;

    private static string InstanceName(string root)
    {
        var name = System.IO.Path.GetFileName(root);
        return name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase) ? "Default"
            : string.IsNullOrWhiteSpace(name) ? "Minecraft" : name;
    }
}
