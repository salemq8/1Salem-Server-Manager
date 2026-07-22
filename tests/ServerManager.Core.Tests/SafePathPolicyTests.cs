using ServerManager.Core;

namespace ServerManager.Core.Tests;

public sealed class SafePathPolicyTests
{
    [Fact]
    public void ResolveWithinRoot_AllowsChildPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "1SalemRoot");

        var resolved = SafePathPolicy.ResolveWithinRoot(root, Path.Combine("world", "level.dat"));

        Assert.True(SafePathPolicy.IsWithinRoot(resolved, root));
    }

    [Fact]
    public void ResolveWithinRoot_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "1SalemRoot");

        Assert.Throws<UnauthorizedAccessException>(
            () => SafePathPolicy.ResolveWithinRoot(root, Path.Combine("..", "outside.txt")));
    }
}

