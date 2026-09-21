using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.StaticFiles;
using PetPotty.Services;

namespace PetPotty.Pages;

public class PetImageModel(IPetService pets, IPetImageStorage storage) : PageModel
{
    public IActionResult OnGet(string token)
    {
        if (!int.TryParse(HttpContext.Session.GetString("userID"), out var userID))
            return Unauthorized();

        if (string.IsNullOrWhiteSpace(token) || token.IndexOfAny(['/', '\\']) >= 0)
            return NotFound();

        var relativePath = pets.GetPetsByUser(userID)
            .Select(pet => pet.ProfileImagePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .FirstOrDefault(path => storage.GetAccessToken(path!) == token);
        if (relativePath == null)
            return NotFound();
        var path = storage.ResolvePhysicalPath(relativePath);
        if (path == null)
            return NotFound();
        if (!System.IO.File.Exists(path))
            return NotFound();

        new FileExtensionContentTypeProvider().TryGetContentType(path, out var contentType);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, contentType ?? "application/octet-stream");
    }
}
