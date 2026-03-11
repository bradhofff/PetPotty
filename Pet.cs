// ============================================================
// Models/Pet.cs
// In .NET Framework models were often nested inside controllers
// or code-behind files. In .NET Core best practice is a dedicated
// /Models folder so they can be shared across pages and services.
// ============================================================

namespace PetPotty.Models
{
    public class Pet
    {
        public int PetID { get; set; }

        // "= string.Empty" is .NET Core nullable-aware coding style.
        // .NET Core 6+ enables nullable reference types by default in new projects.
        // This tells the compiler "this will never be null" and avoids warnings.
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Breed { get; set; } = string.Empty;
        public string Age { get; set; } = string.Empty;
        public DateTime Birthdate { get; set; }
        public string Gender { get; set; } = string.Empty;
        public DateTime createdAt { get; set; }
    }
}