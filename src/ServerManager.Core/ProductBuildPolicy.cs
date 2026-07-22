namespace ServerManager.Core;

public readonly record struct ProductBuildIdentity(
    string ProductVersion,
    int BuildRevision,
    string? PackageSha256 = null)
{
    public ProductBuildIdentity Validate()
    {
        _ = SemanticVersion.Parse(ProductVersion);
        if (BuildRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(BuildRevision),
                "A build revision cannot be negative.");
        }

        if (PackageSha256 is { Length: > 0 } hash &&
            (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
        {
            throw new FormatException("The package SHA-256 is invalid.");
        }

        return this;
    }

    public override string ToString() => $"Version {ProductVersion} Build {BuildRevision}";
}

public enum ProductBuildDisposition
{
    UpdateAvailable = 1,
    AlreadyCurrent = 2,
    DowngradeRejected = 3,
    RepairAllowed = 4,
    SameBuildHashMismatch = 5
}

public sealed record ProductBuildDecision(
    ProductBuildDisposition Disposition,
    ProductBuildIdentity Installed,
    ProductBuildIdentity Available,
    string Message)
{
    public bool CanInstall => Disposition is
        ProductBuildDisposition.UpdateAvailable or
        ProductBuildDisposition.RepairAllowed;
}

public static class ProductBuildPolicy
{
    public static ProductBuildDecision Evaluate(
        ProductBuildIdentity installed,
        ProductBuildIdentity available,
        bool repairMode = false)
    {
        installed.Validate();
        available.Validate();
        var productComparison = SemanticVersion.Parse(available.ProductVersion)
            .CompareTo(SemanticVersion.Parse(installed.ProductVersion));
        if (productComparison > 0)
        {
            return Decision(ProductBuildDisposition.UpdateAvailable,
                $"{available} is newer than installed {installed}.");
        }

        if (productComparison < 0 || available.BuildRevision < installed.BuildRevision)
        {
            return Decision(ProductBuildDisposition.DowngradeRejected,
                $"Update rejected: available {available} is older than installed {installed}.");
        }

        if (available.BuildRevision > installed.BuildRevision)
        {
            return Decision(ProductBuildDisposition.UpdateAvailable,
                $"Internal Build {available.BuildRevision} is newer than installed Build {installed.BuildRevision}.");
        }

        if (repairMode)
        {
            return Decision(ProductBuildDisposition.RepairAllowed,
                $"Explicit repair mode permits reinstalling {available}.");
        }

        var hashesDiffer = !string.IsNullOrWhiteSpace(installed.PackageSha256) &&
            !string.IsNullOrWhiteSpace(available.PackageSha256) &&
            !installed.PackageSha256.Equals(
                available.PackageSha256,
                StringComparison.OrdinalIgnoreCase);
        return hashesDiffer
            ? Decision(ProductBuildDisposition.SameBuildHashMismatch,
                $"{available} matches the installed identity but its package hash differs; explicit repair mode is required.")
            : Decision(ProductBuildDisposition.AlreadyCurrent,
                $"{available} is already current.");

        ProductBuildDecision Decision(
            ProductBuildDisposition disposition,
            string message) => new(disposition, installed, available, message);
    }
}
