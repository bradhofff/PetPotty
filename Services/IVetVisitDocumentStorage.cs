namespace PetPotty.Services
{
    public interface IVetVisitDocumentStorage
    {
        string? Validate(IFormFile? document);
        Task<string> SaveAsync(int vetVisitID, IFormFile document, CancellationToken cancellationToken = default);
        string? ResolvePhysicalPath(string? storedPath);
        void Delete(string? storedPath);
    }
}
