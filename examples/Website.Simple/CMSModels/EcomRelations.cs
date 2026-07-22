using Relatude.CMS;
using Relatude.DB.Datamodels;
using Relatude.DB.Native;
using Relatude.DB.Nodes;

namespace Relatude.Ecom.Models {

    //done
    public class ShopOrders : OneToMany<IShop, IOrder> {
        public class Shop : One { }
        public class Orders : Many { }
    }

    public class CurrencyCountries: OneToMany<ICurrency, IEcomCountry> {
        public class Currency : One { }
        public class Countries : Many { }
    }
    public class OrderItems : OneToMany<IOrder, IOrderItem> {
        public class Order : One { }
        public class Items : Many { }
    }
    //done
    public class ShopsCurrencies : ManyToMany<IShop, ICurrency> {
        public class Shops : ManyFrom { }
        public class Currencies : ManyTo { }
    }

    public class CurrencyOrders : OneToMany<ICurrency, IOrder> {
        public class Currency : One { }
        public class Orders : Many { }
    }

    //done
    public class ShopsPaymentMethods : ManyToMany<IShop, IPaymentMethod> {
        public class Shops : ManyFrom { }
        public class PaymentMethods : ManyTo { }
    }

    //done
    public class ShopTaxClasses : ManyToMany<IShop, ITaxClass> {
        public class Shops : ManyFrom { }
        public class TaxClasses : ManyTo { }
    }

    //done
    public class TaxClassProducts : OneToMany<ITaxClass, IProduct> {
        public class TaxClass : One { }
        public class Products : Many { }
    }

    //done
    public class TaxClassTaxRates : OneToMany<ITaxClass, ITaxRate> {
        public class TaxClass : One { }
        public class TaxRates : Many { }
    }


    public class TaxRatesCountries : ManyToMany<ITaxRate, IEcomCountry> {
        public class TaxRate : ManyFrom { }
        public class Countries : ManyTo { }

    }

    //done
    public class ProductSimpleVariants : OneToMany<IProduct, ISimpleVariant> {
        public class Product : One { }
        public class SimpleVariants : Many { }

    }

    //done
    public class ProductVariants : OneToMany<IProduct, IProduct> {
        public class MainVariant : One { }
        public class SubVariants : Many { }

    }
    public class CategorySubCategories : OneToMany<IProductCategory, IProductCategory> {
        public class ParentCategory : One { }
        public class SubCategories : Many { }
    }
    public class SalesChannelOrders : OneToMany<ISalesChannel, IOrder> {
        public class SalesChannel : One { }
        public class Orders : Many { }
    }
    public class EcomUserOrders : OneToMany<IEcomUser, IOrder> {
        public class Customer : One { }
        public class Orders : Many { }
    }
    public class ProductCategoryProducts : OneToMany<IProductCategory, IProduct> {
        public class Category : One { }
        public class Products : Many { }             
        
    }

    public class ShopsShippingMethods : ManyToMany<IShop, IShippingMethod> {
        public class Shops : ManyFrom { }
        public class ShippingMethods : ManyTo { }
    }
    public class BrandProducts : OneToMany<IBrand, IProduct> {
        public class Brand : One { }
        public class Products : Many { }
    }
    public class  DefaultTaxClassShops: OneToMany<ITaxClass,IShop> {
        public class TaxClass : One { }
        public class Shops : Many { }

    }
    public class  ShippingTaxClassShops :OneToMany<ITaxClass,IShop> {
        public class TaxClass : One { }
        public class Shops : Many { }
        
    }
    public class DefaultCurrencyShops : OneToMany<ICurrency, IShop> {
        public class Currency : One { }
        public class Shops : Many { }
    }
    public class PaymentMethodOrders : OneToMany<IPaymentMethod, IOrder> {
        public class PaymentMethod : One { }
        public class Orders : Many { }
    }
    public class ShippingMethodOrders : OneToMany<IShippingMethod, IOrder> {
        public class ShippingMethod : One { }
        public class Orders : Many { }
    }
    public class ProductOrderItems : OneToMany<IProduct, IOrderItem> {
        public class Product : One { }
        public class OrderItems : Many { }
    }
    public class SimpleVariantsOrderItems : ManyToMany<ISimpleVariant, IOrderItem> {
        public class SimpleVariant : ManyFrom { }
        public class OrderItems : ManyTo { }
    }
    public class ShopDiscounts : OneToMany<IShop, IDiscount> {
        public class Shop : One { }
        public class Discounts : Many { }
    }
    public class DiscountsOrders : ManyToMany<IDiscount, IOrder> {
        public class Discounts : ManyFrom { }
        public class Orders : ManyTo { }
    }
    public class DiscountsProducts : ManyToMany<IDiscount, IProduct> {
        public class Discounts : ManyFrom { }
        public class Products : ManyTo { }
    }
    public class SalesChannelsDiscounts : ManyToMany<ISalesChannel, IDiscount> {
        public class SalesChannels : ManyFrom { }
        public class Discounts : ManyTo { }
    }
}

//public interface ISystemUser {
//    Guid Id { get; set; }
//    SystemUserType UserType { get; set; }
//    UsersToGroups.Groups Memberships { get; }
//}
//[Node(Id = NodeConstants.BaseUserGroupIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
//public interface ISystemUserGroup {
//    Guid Id { get; set; }
//    string GroupName { get; set; }
//    UsersToGroups.Users UserMembers { get; }
//    GroupsToGroups.Memberships GroupMemberships { get; }
//    GroupsToGroups.Members GroupMembers { get; }
//}
//[Node(Id = NodeConstants.BaseCollectionIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
//public interface ISystemCollection {
//    Guid Id { get; set; }
//    string? Name { get; set; }
//    CollectionsToCultures.Cultures Cultures { get; }
//}
//[Node(Id = NodeConstants.BaseCultureIdString, TextIndex = BoolValue.False, SemanticIndex = BoolValue.False)]
//public interface ISystemCulture {
//    Guid Id { get; set; }
//    string CultureCode { get; set; }
//    string NativeName { get; set; }
//    string EnglishName { get; set; }
//    CollectionsToCultures.Collections Collections { get; }
//}
//[Relation(Id = NodeConstants.RelationUsersToGroupsString)]
//public class UsersToGroups : ManyToMany<ISystemUser, ISystemUserGroup> {
//    public class Users : ManyFrom { }
//    public class Groups : ManyTo { }
//}
//[Relation(Id = NodeConstants.RelationGroupsToGroupsString, DisallowCircularReferences = true)]
//public class GroupsToGroups : ManyToMany<ISystemUserGroup, ISystemUserGroup> {
//    public class Memberships : ManyFrom { }
//    public class Members : ManyTo { }
//}
//[Relation(Id = NodeConstants.RelationCollectionsToCulturesString)]
//public class CollectionsToCultures : ManyToMany<ISystemCollection, ISystemCulture> {
//    public class Collections : ManyFrom { }
//    public class Cultures : ManyTo { }
//}

