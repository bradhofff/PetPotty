namespace PetPotty.Services
{
    public interface IPetImageStorage
    {
        string? Validate(IFormFile? image);
        Task<string> SaveAsync(int petID, IFormFile image, CancellationToken cancellationToken = default);
        void Delete(string? relativePath);
    }
}
