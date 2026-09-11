using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.StaticFiles;
using PetPotty.Services;

namespace PetPotty.Pages;

public class PetImageModel(IPetService pets, IConfiguration configuration, IWebHostEnvironment environment) : PageModel
{
    public IActionResult OnGet(string fileName)
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0
            || !pets.GetPetsByUser(userID).Any(pet => pet.ProfileImagePath == $"/uploads/pets/{fileName}"))
            return NotFound();

        var configuredRoot = configuration["PetImages:UploadRoot"]
            ?? (environment.IsDevelopment() ? "uploads" : "/var/www/petpotty/uploads");
        var root = Path.GetFullPath(configuredRoot, environment.ContentRootPath);
        var path = Path.Combine(root, "pets", fileName);
        if (!System.IO.File.Exists(path))
            return NotFound();

        new FileExtensionContentTypeProvider().TryGetContentType(path, out var contentType);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, contentType ?? "application/octet-stream");
    }
}
