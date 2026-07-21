using Relatude.DB.Query;
using Relatude.DB.Utils;
using static Tests.QueryTestHelpers;

namespace Tests;

[TestClass]
public class ArithmeticAndPrecedenceTests {

    [TestMethod]
    public void TestWhereArithmeticComparedToLinq() {
        var store = OpenStoreWithArticles(200);
        var all = store.Query<Article>().ToList();

        { // multiplication and addition
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum * 2 + 1 > 7).Count();
            var fromLinq = all.Count(c => c.IntegerNum * 2 + 1 > 7);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // division
            var fromStore = store.Query<Article>().Where(c => c.DoubleNum / 2.0 < 2.5).Count();
            var fromLinq = all.Count(c => c.DoubleNum / 2.0 < 2.5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // subtraction
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum - 5 >= 0).Count();
            var fromLinq = all.Count(c => c.IntegerNum - 5 >= 0);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // unary minus on property
            var fromStore = store.Query<Article>().Where(c => -c.IntegerNum + 10 > 5).Count();
            var fromLinq = all.Count(c => -c.IntegerNum + 10 > 5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // arithmetic on both sides
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum * 2 > c.IntegerNum + 3).Count();
            var fromLinq = all.Count(c => c.IntegerNum * 2 > c.IntegerNum + 3);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestArithmeticPrecedenceComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        // multiplication binds tighter than addition
        AssertSameNodes(store, all, c => c.IntegerNum + c.IntegerNum * 3 > 20);

        // division binds tighter than subtraction
        AssertSameNodes(store, all, c => c.DoubleNum - c.DoubleNum / 2 > 2);

        // left associativity of subtraction: (x - 1) - 2, not x - (1 - 2)
        AssertSameNodes(store, all, c => c.DoubleNum - 1 - 2 > 0);

        // left associativity of division: (x / 2) / 2, not x / (2 / 2)
        AssertSameNodes(store, all, c => c.DoubleNum / 2 / 2 > 1);

        // long mixed chain: * and / grouped before - and +
        AssertSameNodes(store, all, c => c.IntegerNum * 2 - c.DoubleNum / 2 + c.IntegerNum * c.IntegerNum > 15);

        // unary minus mixed into arithmetic
        AssertSameNodes(store, all, c => -c.IntegerNum * 2 + 20 > c.DoubleNum);
        AssertSameNodes(store, all, c => 10 - -c.IntegerNum > 12);

        { // distribution identity, must match every node: n * 2 + n == n * 3
            var count = store.Query<Article>().Where(c => c.IntegerNum * 2 + c.IntegerNum == c.IntegerNum * 3).Count();
            Assert.AreEqual(all.Count, count);
        }

        { // integer division semantics must match C#: n / 2 * 2 == n only for even n
            var fromStore = store.Query<Article>().Where(c => c.IntegerNum / 2 * 2 == c.IntegerNum).Count();
            var fromLinq = all.Count(c => c.IntegerNum / 2 * 2 == c.IntegerNum);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // integer division identity: n - n / 2 == (n + 1) / 2, true for all n >= 0
            var count = store.Query<Article>().Where(c => c.IntegerNum - c.IntegerNum / 2 == (c.IntegerNum + 1) / 2).Count();
            Assert.AreEqual(all.Count, count);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestBracketsComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        // brackets overriding precedence: (n + 2) * 3 vs n + 2 * 3
        AssertSameNodes(store, all, c => (c.IntegerNum + 2) * 3 > 12);
        AssertSameNodes(store, all, c => c.IntegerNum + 2 * 3 > 12);

        // brackets on the right side of minus: x - (n - 3) must NOT collapse to x - n - 3
        AssertSameNodes(store, all, c => c.DoubleNum - (c.IntegerNum - 3) > 4);

        { // x - (x - 1) is always 1, so > 0.5 must match every node
            var count = store.Query<Article>().Where(c => c.DoubleNum - (c.DoubleNum - 1) > 0.5).Count();
            Assert.AreEqual(all.Count, count);
        }

        { // x / (x * 2) is always 0.5, so < 0.75 must match every node
            var count = store.Query<Article>().Where(c => c.DoubleNum / (c.DoubleNum * 2) < 0.75).Count();
            Assert.AreEqual(all.Count, count);
        }

        { // binomial identity with nested brackets: (n + 1)^2 == n^2 + 2n + 1, must match every node
            var count = store.Query<Article>().Where(c =>
                (c.IntegerNum + 1) * (c.IntegerNum + 1) - (c.IntegerNum * c.IntegerNum + 2 * c.IntegerNum + 1) == 0).Count();
            Assert.AreEqual(all.Count, count);
        }

        // brackets multiplied by brackets
        AssertSameNodes(store, all, c => (c.DoubleNum - 1) * (c.IntegerNum + 2) > 10);

        // bracketed numerator and denominator
        AssertSameNodes(store, all, c => (c.DoubleNum + c.IntegerNum) / (c.IntegerNum + 1) > 1.5);

        // unary minus applied to a bracket
        AssertSameNodes(store, all, c => -(c.IntegerNum - 5) > 0);

        // deeply nested brackets
        AssertSameNodes(store, all, c => (((c.DoubleNum + 1) * 2 - 2) / 2 + (c.IntegerNum - (c.IntegerNum - 1))) * 2 > c.DoubleNum + 3);

        store.Dispose();
    }

    [TestMethod]
    public void TestLogicalPrecedenceComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        // && binds tighter than ||: a || b && c means a || (b && c)
        AssertSameNodes(store, all, c => c.IntegerNum > 8 || c.IntegerNum < 2 && c.DoubleNum > 5);

        // same expression with explicit brackets changing the meaning
        AssertSameNodes(store, all, c => (c.IntegerNum > 8 || c.IntegerNum < 2) && c.DoubleNum > 5);

        // a && b || c && d groups as (a && b) || (c && d)
        AssertSameNodes(store, all, c => c.IntegerNum > 2 && c.IntegerNum < 5 || c.IntegerNum > 7 && c.DoubleNum < 5);

        // negated bracket combined with ||
        AssertSameNodes(store, all, c => !(c.IntegerNum > 3 && c.DoubleNum < 5) || c.Size == Sizes.Small);

        { // double negation is identity
            var fromStore = store.Query<Article>().Where(c => !(!(c.IntegerNum > 5))).Count();
            var fromLinq = all.Count(c => c.IntegerNum > 5);
            Assert.AreEqual(fromLinq, fromStore);
        }

        // arithmetic comparisons as operands of logical operators
        AssertSameNodes(store, all, c => c.IntegerNum + 3 > 5 && c.DoubleNum * 2 < 15 || !(c.Size == Sizes.Large));

        // De Morgan: !(a || b) == !a && !b, both sides must select the same nodes
        var lhs = store.Query<Article>().Where(c => !(c.IntegerNum < 3 || c.DoubleNum > 7)).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        var rhs = store.Query<Article>().Where(c => !(c.IntegerNum < 3) && !(c.DoubleNum > 7)).Execute().Select(c => c.Id).OrderBy(i => i).ToList();
        CollectionAssert.AreEqual(lhs, rhs);

        store.Dispose();
    }

    [TestMethod]
    public void TestQueryStringPrecedenceParsing() {
        // String lambdas bypass C# compile-time constant folding, so these exercise
        // the query language parser's own precedence and bracket handling.
        var store = OpenStoreWithArticles(100);
        var all = store.Query<Article>().ToList();

        // each predicate is a tautology if parsing is correct, so it must match every node
        string[] tautologies = [
            "c => 2 + 3 * 4 == 14",                                    // * before +
            "c => (2 + 3) * 4 == 20",                                  // brackets override
            "c => 10 - 2 - 3 == 5",                                    // left associative -
            "c => 100 / 5 / 2 == 10",                                  // left associative /
            "c => 10 - (2 - 3) == 11",                                 // bracket on right of -
            "c => 10 - 2 * 3 == 4",                                    // * before -
            "c => (10 - 2) * 3 == 24",                                 // brackets override
            "c => 2 * 3 + 4 * 5 == 26",                                // two products summed
            "c => 100 / (5 / 5 + 4) == 20",                            // nested division in bracket
            "c => c.IntegerNum * 2 + 1 == c.IntegerNum + c.IntegerNum + 1",     // property identity
            "c => c.IntegerNum - (c.IntegerNum - 2) == 2",                      // right bracket with property
        ];
        foreach (var predicate in tautologies) {
            var count = store.Query<Article>().Where(predicate).Count();
            Assert.AreEqual(all.Count, count, "Expected tautology to match all nodes: " + predicate);
        }

        { // string lambda vs identical C# lambda
            var fromStore = store.Query<Article>().Where("c => c.IntegerNum > 2 && c.IntegerNum < 8 || c.DoubleNum > 9").Count();
            var fromLinq = all.Count(c => c.IntegerNum > 2 && c.IntegerNum < 8 || c.DoubleNum > 9);
            Assert.AreEqual(fromLinq, fromStore);
        }

        { // convoluted string lambda vs identical C# lambda
            var fromStore = store.Query<Article>()
                .Where("c => ((c.IntegerNum + 2) * 3 - 4) / 2 > c.DoubleNum - (2 - c.IntegerNum) && !(c.IntegerNum == 5 || c.DoubleNum < 1) || c.DoubleNum * 2 - 8 > 10 - (c.IntegerNum - 2) * 4")
                .Count();
            var fromLinq = all.Count(c => ((c.IntegerNum + 2) * 3 - 4) / 2 > c.DoubleNum - (2 - c.IntegerNum) && !(c.IntegerNum == 5 || c.DoubleNum < 1) || c.DoubleNum * 2 - 8 > 10 - (c.IntegerNum - 2) * 4);
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }

    [TestMethod]
    public void TestConvolutedExpressionsComparedToLinq() {
        var store = OpenStoreWithArticles(300);
        var all = store.Query<Article>().ToList();

        // arithmetic heavy: nested brackets, mixed int/double, division by expressions bounded away from zero
        AssertSameNodes(store, all, c =>
            ((c.DoubleNum + 1) * (c.IntegerNum + 2) - (c.DoubleNum * c.DoubleNum - c.IntegerNum * 3) / (c.DoubleNum + 2)) / 2
                - (c.DoubleNum - (c.IntegerNum - (c.DoubleNum - 3)))
            > c.IntegerNum + c.DoubleNum / (c.IntegerNum + 1) - 2);

        // logic heavy: negated brackets, nested right-side brackets inside arithmetic, enum comparison
        AssertSameNodes(store, all, c =>
            !(c.IntegerNum > 7 && c.DoubleNum < 3) && (c.IntegerNum * 2 - 3 > c.DoubleNum || !(c.Size == Sizes.Small))
            || c.DoubleNum * (c.IntegerNum - (c.IntegerNum - 2)) > 15 && !(c.IntegerNum < 2 || c.IntegerNum > 8));

        // everything at once: three-level bracket nesting on both sides of the comparison
        AssertSameNodes(store, all, c =>
            (((c.IntegerNum + 1) * (c.DoubleNum + 2) - (c.IntegerNum * c.IntegerNum - 1)) / (c.DoubleNum + 5) + 1) * 2
                - (10 - (c.IntegerNum - (2 - c.DoubleNum)))
            > ((c.DoubleNum - (c.IntegerNum - 3)) * 2 + c.IntegerNum) / 3
            && !(c.DoubleNum - (c.DoubleNum - c.IntegerNum) == 0 && c.IntegerNum > 0));

        { // convoluted sum with nested brackets, compared with tolerance
            System.Linq.Expressions.Expression<Func<Article, double>> sumExpression = x =>
                ((x.DoubleNum + 1) * 2 - (x.IntegerNum - 3) / 4.0) * ((x.IntegerNum + 2) * (x.DoubleNum - 5) + 100) / ((x.DoubleNum + 10) * 2)
                    - (x.DoubleNum - (x.IntegerNum - (x.DoubleNum - 1)));
            var fromStore = store.Query<Article>().Sum(sumExpression);
            var fromLinq = all.Sum(sumExpression.Compile());
            Assert.AreEqual(fromLinq, fromStore, 1e-6);
        }

        { // convoluted sum, integer arithmetic only, must match exactly
            System.Linq.Expressions.Expression<Func<Article, int>> sumExpression = x =>
                (x.IntegerNum + 3) * (x.IntegerNum - (x.IntegerNum - 2)) - (x.IntegerNum * x.IntegerNum - x.IntegerNum) / (x.IntegerNum + 1);
            var fromStore = store.Query<Article>().Sum(sumExpression);
            var fromLinq = all.Sum(sumExpression.Compile());
            Assert.AreEqual(fromLinq, fromStore);
        }

        store.Dispose();
    }
}
