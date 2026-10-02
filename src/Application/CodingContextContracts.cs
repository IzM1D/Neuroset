namespace Neuroset.Application;

public sealed record CodingInstructionFile(
    string Path,
    string Scope,
    string Content,
    bool IsGlobal);

public sealed record CodingInstructionReference(
    string Path,
    string Scope);

public sealed record CodingSkillDescriptor(
    string Id,
    string Name,
    string Description,
    string Path,
    bool IsGlobal);

public sealed record CodingSkillDocument(
    CodingSkillDescriptor Descriptor,
    string Content);

public sealed record CodingContextSnapshot(
    string WorkspacePath,
    string RepositoryRoot,
    IReadOnlyList<CodingInstructionFile> EffectiveInstructions,
    IReadOnlyList<CodingInstructionReference> NestedInstructions,
    IReadOnlyList<CodingSkillDescriptor> Skills,
    string RepositoryMap);

public interface ICodingContextProvider
{
    Task<CodingContextSnapshot> InspectAsync(
        string workspacePath,
        string? targetPath = null,
        int repositoryMapDepth = 3,
        CancellationToken cancellationToken = default);

    Task<CodingSkillDocument?> ReadSkillAsync(
        string workspacePath,
        string skillId,
        CancellationToken cancellationToken = default);
}
