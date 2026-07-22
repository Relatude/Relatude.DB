using Relatude.CMS.Models;
using Relatude.DB.Nodes;
using Relatude.Ecom.Models;

namespace Ventistaal.Web.Models.RelatudeDB {

    /// <summary>
    /// A product category imported from Bluestone PIM. Inherits <see cref="IProductCategory"/>
    /// (Products plus the ParentCategory/SubCategories hierarchy) and <see cref="IPage"/>
    /// (Name/Address/Template/meta via IItem+IPage), and adds the Bluestone external id.
    /// The category hierarchy uses IProductCategory.ParentCategory/SubCategories — not IPage's
    /// Parent/Children, which are the content-tree position.
    /// </summary>
    public interface IVentiCategory : IProductCategory, IPage {

        // Bluestone category id — stable key for idempotent re-import.
        public string ExternalId { get; set; }

        public OtherProductCategories.Products OtherProducts { get; set; }

    }



    // From = IProduct so a product's OtherCategories (ManyTo) can be set from the product side;
    // the category exposes OtherProducts (ManyFrom).
    public class OtherProductCategories : ManyToMany<IProduct, IProductCategory> {
        public class Products : ManyFrom { }
        public class Categories : ManyTo { }
    }
}
