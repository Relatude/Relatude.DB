using Relatude.DB.AI;
using Relatude.DB.Common;
using Relatude.DB.Datamodels;
using Relatude.DB.Datamodels.Properties;
using Relatude.DB.IO;

namespace Relatude.DB.DataStores.Definitions.PropertyTypes {
    internal class FileProperty : Property {
        DataStoreLocal? _store;
        public FileProperty(FilePropertyModel pm, Definition def) : base(pm, def) {
        }
        internal override void Initalize(DataStoreLocal store, Definition def, SettingsLocal config, IIOProvider io, AIEngine? ai) {
            _store = store;
        }
        public override PropertyType PropertyType => PropertyType.ByteArray;
        public override object ForceValueType(object value, out bool changed) {
            return FilePropertyModel.ForceValueType(value, out changed);
        }
        /// <summary>
        /// A file value written from outside, as a transaction brings it. Only the store puts a stored file into a
        /// property (an upload, or a copy through NodeFileCopyAction), so a value from outside may empty the property,
        /// or change the name, meta, extracted text and pixel size of the file it holds, and nothing else. A value
        /// naming another stored file is not taken, nor any file in a node being inserted: the property keeps what it
        /// has. That also keeps an upload when an object read before it is written back after it.
        /// </summary>
        public override object TransformFromOuterToInnerValue(object value, INodeData? oldNodeData) {
            if (value is not FileValue fileValue) throw new Exception("Value is not a file value. ");
            if (fileValue.IsEmpty) return fileValue; // the file taken out
            if (_store != null && _store.IsStoreMadeFile(fileValue)) return fileValue; // a copy the store made for this transaction (NodeFileCopyAction)
            if (oldNodeData == null) return FileValue.Empty; // an insert, which holds no file yet
            if (!oldNodeData.TryGetValue(Id, out var oldValue) || oldValue is not FileValue oldFile) return FileValue.Empty;
            // another stored file, or a file where there is none: the property keeps its own
            if (oldFile.IsEmpty || oldFile.FileId != fileValue.FileId || oldFile.StorageId != fileValue.StorageId) return oldFile.Copy();
            return FileValue.BringChangesFromOuterToInner(oldFile, fileValue); // the same file: what may change of it
        }
        public override void ValidateValue(object value, INodeData node) { }
        public static object GetValue(byte[] bytes) => BitConverter.ToBoolean(bytes, 0);
        public override bool CanBeFacet() => false;
        public override bool AreValuesEqual(object v1, object v2) {
            var b1 = FilePropertyModel.ForceValueType(v1, out _);
            var b2 = FilePropertyModel.ForceValueType(v2, out _);
            return FileValue.AreValuesEqual(b1, b2);
        }
    }
}
