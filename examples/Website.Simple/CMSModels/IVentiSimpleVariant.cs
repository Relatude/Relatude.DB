using Relatude.Ecom.Models;

namespace Ventistaal.Web.Models.RelatudeDB {

    /// <summary>
    /// A product variant stored via the Relatude.Ecom <c>SimpleVariants</c> mechanism (lightweight
    /// objects holding only what differs between variants — the right fit when there are many
    /// variants with few differences). Extends <see cref="ISimpleVariant"/> with the Bluestone
    /// external id and the variant-level attributes.
    ///
    /// One <see cref="IVentiSimpleVariant"/> is created per Bluestone VARIANT and attached to its
    /// VARIANT_GROUP product through the inherited <c>IProduct.SimpleVariants</c> relation.
    ///
    /// Different variant groups vary by different attributes, so this type carries the union of all
    /// variant-level attributes; each variant populates only the ones relevant to its group and
    /// leaves the rest unset. "Which attributes vary" for a group is therefore implicit in which
    /// properties its variants populate.
    /// </summary>
    public interface IVentiSimpleVariant : ISimpleVariant {

        // Bluestone variant id — the stable key for idempotent re-import. ISimpleVariant carries
        // SKU/GTIN but no external id of its own, so we add one here.
        public string ExternalId { get; set; }
               

        // --- Variant-level attributes ---
        // The definingAttribute=true attributes found across all variant groups (see
        // BluestoneVariantAttributeDiscoveryTask). Mapped by Bluestone attribute id in
        // BluestoneImportProductsTask.MapSimpleVariant. Stored as strings (Bluestone values arrive as
        // string arrays); each variant only populates the attributes its group varies by.
        public string Diameter { get; set; }                 // Diameter Ø
        public string Colors { get; set; }                   // Farger (multi_select / color)
        public string RALCode { get; set; }                  // RAL-kode
        public string Dimension { get; set; }                // Dimensjon
        public string ModelName { get; set; }                // Modellnavn
        public string BrandSupplier { get; set; }            // Merke / Leverandør
        public string Length { get; set; }                   // Lengde (decimal)
        public string Width { get; set; }                    // Bredde (decimal)
        public string AngleInDegrees { get; set; }           // Vinkel (grader)
        public string Height { get; set; }                   // Høyde (decimal)
        public string Diameter2 { get; set; }                // Diameter Ø 2
        public string MaxCapacityKW { get; set; }            // Maks varmekapasitet (kW)
        public string EnergyClass { get; set; }              // Energiklasse (single_select)
        public string MinMaxCoolingCapacity { get; set; }    // Min/maks kjølekapasitet
        public string SCOP { get; set; }                     // SCOP
        public string SurfaceMaterial { get; set; }          // Overflate / Materiale (multi_select / color)
        public string Thicknesses { get; set; }              // Tykkelser
        public string Connection { get; set; }               // Anslutning
        public string BitType { get; set; }                  // Bitstype
        public string WidthMm { get; set; }                  // Bredde (mm)
        public string LengthAngleDiameter2 { get; set; }     // Lengde / Vinkel / Diameter 2
        public string MountingType { get; set; }             // Monteringstype (multi_select)
    }
}
