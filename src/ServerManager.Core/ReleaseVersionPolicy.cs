namespace ServerManager.Core;

public static class ReleaseVersionPolicy
{
    public static ProductBuildDecision EvaluateBuild(
        string targetVersion,
        int targetBuildRevision,
        IEnumerable<ProductBuildIdentity> knownBuilds,
        bool repairSameBuild = false)
    {
        var target = new ProductBuildIdentity(targetVersion, targetBuildRevision).Validate();
        var known = knownBuilds.Select(item => item.Validate()).ToArray();
        if (known.Length == 0)
        {
            return new ProductBuildDecision(
                ProductBuildDisposition.UpdateAvailable,
                new ProductBuildIdentity("0.0.0", 0),
                target,
                $"{target} is the first detected release build.");
        }

        var highest = known.Aggregate((left, right) =>
        {
            var version = SemanticVersion.Parse(left.ProductVersion).CompareTo(
                SemanticVersion.Parse(right.ProductVersion));
            return version > 0 ||
                (version == 0 && left.BuildRevision >= right.BuildRevision)
                ? left
                : right;
        });
        return ProductBuildPolicy.Evaluate(highest, target, repairSameBuild);
    }
}
