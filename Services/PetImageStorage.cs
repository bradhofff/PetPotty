namespace PetPotty.Services
{
    public sealed class PetImageStorage : IPetImageStorage
    {
        public const long MaxFileSize = 2 * 1024 * 1024;

        private static readonly byte[] JpegSignature = [0xFF, 0xD8, 0xFF];
        private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        private readonly string _petsRoot;
        private readonly ILogger<PetImageStorage> _logger;

        public PetImageStorage(IConfiguration configuration, IWebHostEnvironment environment, ILogger<PetImageStorage> logger)
        {
            var configuredRoot = configuration["PetImages:UploadRoot"]
                ?? (environment.IsDevelopment() ? "uploads" : "/var/www/petpotty/uploads");
            var uploadRoot = Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.GetFullPath(configuredRoot, environment.ContentRootPath);
            _petsRoot = Path.Combine(uploadRoot, "pets");
            _logger = logger;
        }

        public string? Validate(IFormFile? image)
        {
            if (image == null)
                return null;

            if (image.Length == 0)
                return "The selected pet photo is empty.";

            if (image.Length > MaxFileSize)
                return "The pet photo must be 2 MB or smaller.";

            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var isJpegExtension = extension is ".jpg" or ".jpeg";
            var isPngExtension = extension == ".png";
            if (!isJpegExtension && !isPngExtension)
                return "Only JPEG and PNG pet photos are allowed.";

            var isJpegContentType = string.Equals(image.ContentType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(image.ContentType, "image/jpg", StringComparison.OrdinalIgnoreCase);
            var isPngContentType = string.Equals(image.ContentType, "image/png", StringComparison.OrdinalIgnoreCase);
            if ((isJpegExtension && !isJpegContentType) || (isPngExtension && !isPngContentType))
                return "The pet photo's file type does not match its extension.";

            Span<byte> header = stackalloc byte[PngSignature.Length];
            using var stream = image.OpenReadStream();
            var bytesRead = 0;
            while (bytesRead < header.Length)
            {
                var read = stream.Read(header[bytesRead..]);
                if (read == 0)
                    break;
                bytesRead += read;
            }
            var hasJpegSignature = bytesRead >= JpegSignature.Length
                && header[..JpegSignature.Length].SequenceEqual(JpegSignature);
            var hasPngSignature = bytesRead >= PngSignature.Length
                && header.SequenceEqual(PngSignature);

            if ((isJpegExtension && !hasJpegSignature) || (isPngExtension && !hasPngSignature))
                return "The selected file is not a valid JPEG or PNG image.";

            return null;
        }

        public async Task<string> SaveAsync(int petID, IFormFile image, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_petsRoot);

            var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            var fileName = $"{petID}_{Guid.NewGuid():N}{extension}";
            var physicalPath = Path.Combine(_petsRoot, fileName);

            await using var output = new FileStream(physicalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await image.CopyToAsync(output, cancellationToken);
            return $"/uploads/pets/{fileName}";
        }

        public void Delete(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return;

            const string expectedPrefix = "/uploads/pets/";
            if (!relativePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ignored unexpected pet image path {ProfileImagePath}", relativePath);
                return;
            }

            var fileName = Path.GetFileName(relativePath);
            if (!string.Equals(relativePath, expectedPrefix + fileName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Ignored unsafe pet image path {ProfileImagePath}", relativePath);
                return;
            }

            try
            {
                var physicalPath = Path.Combine(_petsRoot, fileName);
                if (File.Exists(physicalPath))
                    File.Delete(physicalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete old pet image {ProfileImagePath}", relativePath);
            }
        }
    }
}
