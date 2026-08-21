using System.IO.Compression;

namespace PetPotty.Services
{
    public sealed class VetVisitDocumentStorage : IVetVisitDocumentStorage
    {
        public const long MaxFileSize = 10 * 1024 * 1024;

        private static readonly Dictionary<string, string[]> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = ["application/pdf"],
            [".jpg"] = ["image/jpeg", "image/jpg"],
            [".jpeg"] = ["image/jpeg", "image/jpg"],
            [".png"] = ["image/png"],
            [".doc"] = ["application/msword", "application/octet-stream"],
            [".docx"] = ["application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                         "application/zip", "application/octet-stream"]
        };

        private readonly string _root;
        private readonly ILogger<VetVisitDocumentStorage> _logger;

        public VetVisitDocumentStorage(
            IConfiguration configuration,
            IWebHostEnvironment environment,
            ILogger<VetVisitDocumentStorage> logger)
        {
            var configuredRoot = configuration["VetDocuments:StorageRoot"]
                ?? (environment.IsDevelopment() ? "vet-documents" : "/var/www/petpotty/vet-documents");
            _root = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
            _logger = logger;
        }

        public string? Validate(IFormFile? document)
        {
            if (document == null)
                return "Choose a document to upload.";
            if (document.Length == 0)
                return "The selected document is empty.";
            if (document.Length > MaxFileSize)
                return "Documents must be 10 MB or smaller.";
            if (Path.GetFileName(document.FileName.Replace('\\', '/')).Length > 260)
                return "The document file name is too long.";

            var extension = Path.GetExtension(document.FileName).ToLowerInvariant();
            if (!AllowedContentTypes.TryGetValue(extension, out var contentTypes))
                return "Only PDF, Word, JPEG, and PNG documents are supported.";
            if (!contentTypes.Contains(document.ContentType, StringComparer.OrdinalIgnoreCase))
                return "The document content type does not match its extension.";

            Span<byte> header = stackalloc byte[8];
            using var stream = document.OpenReadStream();
            var count = stream.Read(header);
            var valid = extension switch
            {
                ".pdf" => count >= 5 && header[..5].SequenceEqual("%PDF-"u8),
                ".jpg" or ".jpeg" => count >= 3 && header[..3].SequenceEqual(new byte[] { 0xFF, 0xD8, 0xFF }),
                ".png" => count >= 8 && header.SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
                ".doc" => count >= 8 && header.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
                ".docx" => count >= 4 && header[..4].SequenceEqual(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
                _ => false
            };
            if (valid && extension == ".docx")
            {
                try
                {
                    stream.Position = 0;
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                    valid = archive.GetEntry("[Content_Types].xml") != null
                        && archive.GetEntry("word/document.xml") != null;
                }
                catch (InvalidDataException)
                {
                    valid = false;
                }
            }
            return valid ? null : "The selected file contents are not valid for that document type.";
        }

        public async Task<string> SaveAsync(int vetVisitID, IFormFile document, CancellationToken cancellationToken = default)
        {
            var extension = Path.GetExtension(document.FileName).ToLowerInvariant();
            var visitDirectory = Path.Combine(_root, vetVisitID.ToString());
            Directory.CreateDirectory(visitDirectory);
            var fileName = $"{Guid.NewGuid():N}{extension}";
            var physicalPath = Path.Combine(visitDirectory, fileName);
            await using var output = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await document.CopyToAsync(output, cancellationToken);
            return $"{vetVisitID}/{fileName}";
        }

        public string? ResolvePhysicalPath(string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return null;
            var normalized = storedPath.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Contains("../", StringComparison.Ordinal))
                return null;

            var fullPath = Path.GetFullPath(Path.Combine(_root, normalized));
            var rootWithSeparator = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal) ? fullPath : null;
        }

        public void Delete(string? storedPath)
        {
            var physicalPath = ResolvePhysicalPath(storedPath);
            if (physicalPath == null)
            {
                if (!string.IsNullOrWhiteSpace(storedPath))
                    _logger.LogWarning("Ignored unsafe vet document path {StoredPath}", storedPath);
                return;
            }
            try
            {
                if (File.Exists(physicalPath))
                    File.Delete(physicalPath);
                var visitDirectory = Path.GetDirectoryName(physicalPath);
                if (visitDirectory != null
                    && Directory.Exists(visitDirectory)
                    && !Directory.EnumerateFileSystemEntries(visitDirectory).Any())
                {
                    Directory.Delete(visitDirectory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete vet document {StoredPath}", storedPath);
            }
        }
    }
}
