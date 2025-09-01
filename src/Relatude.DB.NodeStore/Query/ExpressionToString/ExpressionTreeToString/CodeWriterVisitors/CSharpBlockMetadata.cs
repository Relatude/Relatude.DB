using static Relatude.DB.Query.ExpressionToString.ExpressionTreeToString.CodeWriterVisitors.CSharpMultilineBlockTypes;

namespace Relatude.DB.Query.ExpressionToString.ExpressionTreeToString.CodeWriterVisitors {
    internal class CSharpBlockMetadata {
        internal CSharpMultilineBlockTypes BlockType { get; private set; } = Inline;
        internal bool ParentIsBlock { get; private set; } = false;
        internal static CSharpBlockMetadata CreateMetadata(CSharpMultilineBlockTypes blockType = Inline, bool parentIsBlock = false) => new CSharpBlockMetadata {
            BlockType = blockType,
            ParentIsBlock = parentIsBlock
        };
        internal void Deconstruct(out CSharpMultilineBlockTypes blockType, out bool parentIsBlock) {
            blockType = BlockType;
            parentIsBlock = ParentIsBlock;
        }
    }
}
