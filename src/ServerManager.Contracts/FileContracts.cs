namespace ServerManager.Contracts;

public sealed record FileWriteRequest(
    string RelativePath,
    string Content);

public sealed record FileRenameRequest(
    string RelativePath,
    string NewName);

public sealed record FileDeleteRequest(
    string RelativePath,
    string ConfirmationText);
