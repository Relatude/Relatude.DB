using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Relatude.Ecom.Models {
    public interface IProductCategory {
        public CategorySubCategories.ParentCategory ParentCategory { get; set; }
        public CategorySubCategories.SubCategories SubCategories { get; set; }
        public ProductCategoryProducts.Products Products { get; set; }
    }
}
