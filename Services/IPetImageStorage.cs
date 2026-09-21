namespace PetPotty.Services
{
    public interface IPetImageStorage
    {
        string? Validate(IFormFile? image);
        Task<string> SaveAsync(int petID, IFormFile image, CancellationToken cancellationToken = default);
        string GetAccessToken(string relativePath);
        string? ResolvePhysicalPath(string relativePath);
        void Delete(string? relativePath);
    }
}
