using Relatude.CMS.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public  interface ICurrency :IItem{

        public CurrencyCountries.Countries Countries { get; set; }
        public string Code { get; set; }
        public ShopsCurrencies.Shops Shops { get; set; }

        public DefaultCurrencyShops.Shops DefaultCurrencyForShops { get; set; }
    }
}
