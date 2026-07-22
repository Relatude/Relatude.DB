using Relatude.CMS.Models;
using Relatude.DB.Common;
using Relatude.DB.Nodes;
using Relatude.Ecom.Models;

namespace Ventistaal.Web.Models.RelatudeDB {
    public interface IVentiProduct : IProduct {

        public OtherProductCategories.Categories OtherCategories { get; set; }
        public int NRFNo { get; set; }
        public string ModelName { get; set; }
        public string TypeOfProduct { get; set; } //Type produkt
        public bool PriceOnRequest { get; set; }
        public bool ShowOnWeb { get; set; }

        // True when this product is a Bluestone variant group (a container that owns simple variants),
        // set from the Bluestone type == "GROUP" during import. False for standalone (single) products.
        public bool IsVariantGroup { get; set; }

        // Bluestone media id of the currently-imported main image; used to skip re-downloading it
        // when unchanged.
        public string MainImageExternalId { get; set; }

        [EmbeddedMapProperty(KeyProperty = nameof(IBluestoneFile.ExternalId))]
        public EmbeddedMap<string, IBluestoneFile> MorePictures { get; }

        [EmbeddedMapProperty(KeyProperty = nameof(IBluestoneFile.ExternalId))]
        public EmbeddedMap<string, IBluestoneFile> Documents { get; }

        public int AIP { get; set; }
        public string QuantityInBox { get; set; }
        public string QuantityOnPallet { get; set; }
        public Status ProductStatus { get; set; }
        public int NOBBNr { get; set; }
        //responsible relation to person or department
        public decimal Area { get; set; }
        public decimal Volume { get; set; }
        public string ManufacturerName { get; set; }

        public string CobuilderProductId { get; set; }

        public string SurfaceMaterial { get; set; }
        public CorrotionClass CorrosionClass { get; set; }

        public ProductsColors.Colors Colors { get; set; }
        public ProductsSurfaceMaterials.SurfaceMaterials SurfaceMaterials { get; set; }

        public DensityClass DensityClass { get; set; }
        public ProductsOperationTypes.OperationTypes OperationTypes { get; set; }
        public ProductMountingTypes.MountingTypes MountingTypes { get; set; }
        public EnergyClassProducts.EnergyClass EnergyClass { get; set; }
        public ProductsRefrigerants.Refrigerants Refrigerants { get; set; }
        public StockStatusProducts.StockStatus StockStatus { get; set; }
        public DesignConstructionProducts.DesignConstruction DesignConstruction { get; set; }
        public string Diameter { get; set; }
        public string AirVolumeLitersPerSecond { get; set; }       
        public string MaxCapacityKW { get; set; }
        public string Connection { get; set; }
        public string MaximumAirFlowM3PerHour { get; set; }
        public string Dimension { get;set; }
       public string Thicknesses { get; set; }
        public string BrandSupplier { get; set; }
        public string RALCode { get; set; }
        public string MaxPressureDropPA { get; set; }
        public string FireRating { get; set; }
        public string ApprovedForNordicEcolabelledBuildings { get; set; }
        public string AngleTPiece { get; set; }
        public string SCOP { get; set; }
        public string COP { get; set; }
        public string Volatage230or400 { get; set; }
        public string Phase1or3 { get; set; }
        public string SoundLevelOutdoorDB { get; set; }
        public string SoundLevelIndoorDB { get; set; }
        public string MinMaxCoolingCapacity { get; set; }
        public string IncludesBatteryAndCharger { get; set; } // Includes battery and charger
        public string WeightIndoorUnitInKg { get; set; } // Weight indoor unit(kg)
        public string WeightOutdoorUnitInKg { get; set; } // Weight outdoor unit(kg)
        public string HxWxL_IndoorUnit { get; set; } // H x W x L Indoor unit
        public string HxWxL_OutdoorUnit { get; set; } // H x W x L Outdoor unit
        public string MaxTempCelcius { get; set; } // Max temp (°C)
        public string AngleInDegrees { get; set; } // Angle in degrees

    }

    public interface IBluestoneFile {
        public FileValue File { get; set; }
        public string ExternalId { get; set; }
        // Bluestone media labels describing what the file is (e.g. "Produktdatablad", "FDV",
        // "Miljøbilde"), comma-joined. Populated from the media's labels[] during import.
        public string Labels { get; set; }
    }
    public interface IDesignConstruction {
                public string ExternalId { get; set; }
    }
    public interface IStockOverrideStatus : IItem {
        public string ExternalId { get;set; }
    }
    public interface IRefrigerant : IItem {
                public string ExternalId { get; set; }
    }
    public interface IColor : IItem {

        public string ColorCode { get; set; }
        public string ExternalId { get; set; }
    }
    public interface IEnergyClass : IItem {
        public string ExternalId { get; set; }
    }
    public interface ISurfaceMaterial {
        public string ColorCode { get; set; }

        public string ExternalId { get; set; }
    }

    public interface IOperationType : IItem {
        public string ExternalId { get; set; }
    }
    public class DesignConstructionProducts : OneToMany<IDesignConstruction, IVentiProduct> {
        public class Products : Many { }
        public class DesignConstruction : One { }
    }
    public class ProductsRefrigerants : ManyToMany<IVentiProduct, IRefrigerant> {
        public class Products : ManyFrom { }
        public class Refrigerants : ManyTo { }
    }
    public class StockStatusProducts : OneToMany<IStockOverrideStatus, IVentiProduct> {
        public class StockStatus : One { }
        public class Products : Many { }
    }
    public class EnergyClassProducts: OneToMany<IEnergyClass, IVentiProduct> {
        public class EnergyClass : One { }
        public class Products : Many { }
    }
    public class ProductsOperationTypes : ManyToMany<IVentiProduct, IOperationType> {
        public class Products : ManyFrom { }
        public class OperationTypes : ManyTo { }
    }

    public class ProductsColors : ManyToMany<IVentiProduct, IColor> {
        public class Products : ManyFrom { }
        public class Colors : ManyTo { }
    }
    public class ProductsSurfaceMaterials : ManyToMany<IVentiProduct, ISurfaceMaterial> {
        public class Products : ManyFrom { }
        public class SurfaceMaterials : ManyTo { }
    }
    public class ProductMountingTypes : ManyToMany<IVentiProduct, IMountingType> {
        public class Products : ManyFrom { }
        public class MountingTypes : ManyTo { }
    }

    public enum DensityClass {
        A = 1,
        B = 2,
        B_EN1751 = 3,
        C = 4,
        D = 5,
        ATC_1 = 6,
        ATC_2 = 7,
        ATC_3 = 8,
        ATC_4 = 9,
        IP44 = 10,
        IP54 = 11,
        Class_C_EN1751 = 12,
        Class_C_EN1752 = 13,
        MuAbove3000 = 14
    }
    public enum CorrotionClass {
        C1 = 1,
        C2 = 2,
        C3 = 3,
        C4 = 4,
        C5 = 5,
        CX = 6,
        C5C4 = 7,
        CC4 = 8
    }

    public enum Status {
        Active = 0,
        Discontinued = 9,
        ClearOut = 3,
        Expired = 2,
        CanBeOrdered = 1
    }

    public interface IMountingType : IItem {
        public string ExternalId { get; set; }
    }


}
