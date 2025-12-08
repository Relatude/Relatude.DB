export class Expression {
    private exp: string;
    constructor(exp?: string) {
        this.exp = exp || "";
    }
    and(expression1: string): Expression {
        this.exp += " && " + expression1;
        return this;
    }
    or(expression1: string): Expression {
        this.exp += " || " + expression1;
        return this;
    }
    static and(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = "(" + expression1 + ") && (" + expression2 + ")";
        return exp;
    }
    static or(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = "(" + expression1 + ") || (" + expression2 + ")";
        return exp;
    }
    static equal(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " == " + expression2;
        return exp;
    }
    static notEqual(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " != " + expression2;
        return exp;
    }
    static greaterThan(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " > " + expression2;
        return exp;
    }
    static greaterThanOrEqual(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " >= " + expression2;
        return exp;
    }
    static lessThan(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " < " + expression2;
        return exp;
    }
    static lessThanOrEqual(expression1: any, expression2: any): Expression {
        var exp = new Expression();
        exp.exp = expression1 + " <= " + expression2;
        return exp;
    }
    static not(expression1: any): Expression {
        var exp = new Expression();
        exp.exp = "!" + expression1;
        return exp;
    }
    toString() {
        return this.exp;
    }
}
