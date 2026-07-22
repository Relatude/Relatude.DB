using Relatude.CMS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IEcomCountry :IItem{
        TaxRatesCountries.TaxRate TaxRate { get; set; }
        CurrencyCountries.Currency Currency { get; set; }
    }
}
