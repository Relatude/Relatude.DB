using Microsoft.CodeAnalysis;
using Relatude.CMS.Models;
using Relatude.DB.Common;
using Relatude.DB.Nodes;

namespace Relatude.Ecom.Models {
    public interface IProduct :  IItem, IPage{

        FileValue MainProductImage { get;  set; }
        string ArticleNumber { get; set; }

        [StringProperty(Indexed =true)]
        string ExternalId { get; set; }
        public decimal Price { get; set; }
        [IntegerProperty(Indexed = true)]
        public int NumberInStock { get; set; }
        public string ShortDescription { get; set; }
        public string Description { get; set; }
        public string GTIN { get; set; }
        public decimal Weight{ get; set; }
        public decimal NetWeight { get; set; }
        public WeightUnit WeightUnit { get; set; }
        public decimal Width{ get; set; }
        public decimal Height { get; set; }
        public decimal Length { get; set; }
        public LengthUnit LengthUnit { get; set; }
        [EmbeddedMapProperty(KeyProperty = nameof(IAlternativeCurrencyPrice.CurrencyCode))]
        public EmbeddedMap<string,IAlternativeCurrencyPrice> AlternativePrices { get; } 
        public TaxClassProducts.TaxClass TaxClass { get; set;   }
        public ProductSimpleVariants.SimpleVariants SimpleVariants { get; set; }
        public ProductCategoryProducts.Category Category { get; set; }
        public ProductVariants.SubVariants Variants { get; set; }
        public ProductVariants.MainVariant MainVariant { get; set; }
        public BrandProducts.Brand Brand { get; set; }
        public bool Subvariant {  get; set; }
    }
    public interface IAlternativeCurrencyPrice {
        [StringProperty(Indexed = true)]
        public string CurrencyCode { get; set;  }
        public decimal Price { get; set; } 
    }
    public enum LengthUnit {
        Millimeter =0,
        Centimeter =1,
        Meter =2,        
        Inch =3
    }
    public enum WeightUnit {
        Gram = 0,
        Kilogram = 1,
        Pound = 2,
        Ounce = 3
    }
}
