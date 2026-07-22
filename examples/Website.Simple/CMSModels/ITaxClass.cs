using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface ITaxClass {

        TaxClassProducts.Products Products { get; }
       ShopTaxClasses.Shops Shops { get; }

        TaxClassTaxRates.TaxRates TaxRates { get; }

        DefaultTaxClassShops.Shops DefaultForShops { get; }
        ShippingTaxClassShops.TaxClass ShippingTaxClassForShops { get; }
    }
}
