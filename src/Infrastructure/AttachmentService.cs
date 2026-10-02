using Neuroset.Application;
using Neuroset.Domain;

namespace Neuroset.Infrastructure;

public sealed class AttachmentService(IKnowledgeService knowledge, IKnowledgeRepository knowledgeRepository) : IAttachmentService
{
    private static readonly HashSet<string> KnowledgeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".txt", ".md", ".docx", ".csv", ".json" };
    private static readonly string AttachmentDirectory = Path.Combine(Neuroset.Application.ApplicationPaths.DataDirectory, "Attachments");
    public async Task<AttachmentInfo> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var sourcePath=Path.GetFullPath(path);if(!File.Exists(sourcePath))throw new FileNotFoundException("Attachment not found.",sourcePath);var info=new FileInfo(sourcePath);if(info.Length>100L*1024*1024)throw new InvalidOperationException("Attachments are limited to 100 MB.");
        Directory.CreateDirectory(AttachmentDirectory);var id=Guid.NewGuid();var target=Path.Combine(AttachmentDirectory,id.ToString("N")+info.Extension.ToLowerInvariant());
        await using(var source=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read,81920,FileOptions.Asynchronous|FileOptions.SequentialScan))await using(var destination=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous|FileOptions.WriteThrough))await source.CopyToAsync(destination,cancellationToken).ConfigureAwait(false);
        Guid? knowledgeId=null;if(KnowledgeExtensions.Contains(info.Extension)){var indexed=await knowledge.IndexAsync(target,cancellationToken).ConfigureAwait(false);knowledgeId=indexed.Id;}
        return new(id,info.Name,target,GetMediaType(info.Extension),info.Length,DateTimeOffset.UtcNow,knowledgeId);
    }

    public async Task DeleteAsync(AttachmentInfo attachment, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(attachment.LocalPath);
        var relative = Path.GetRelativePath(AttachmentDirectory, fullPath);
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException("Only application-owned attachment files can be removed.");

        if (attachment.KnowledgeSourceId is { } knowledgeSourceId)
            await knowledgeRepository.DeleteSourceAsync(knowledgeSourceId, cancellationToken).ConfigureAwait(false);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }
    private static string GetMediaType(string extension)=>extension.ToLowerInvariant() switch{".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".webp"=>"image/webp",".gif"=>"image/gif",".pdf"=>"application/pdf",".docx"=>"application/vnd.openxmlformats-officedocument.wordprocessingml.document",".md"=>"text/markdown",".csv"=>"text/csv",".json"=>"application/json",_=>"text/plain"};
}
