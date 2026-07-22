using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public  interface ISimpleVariant {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public IProduct Product { get; set; }
        public string SKU { get; set; }
        public string GTIN { get; set; }
        public int NumberInStock { get; set; }
        public decimal Price { get; set; }
        public bool SamePriceAsMainVariant { get; set; }
    }
}
