using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface ITaxRate {

        TaxClassTaxRates.TaxClass TaxClass { get; }
        decimal Rate { get; set; }
        TaxRatesCountries.Countries Countries { get; }
    }
}
