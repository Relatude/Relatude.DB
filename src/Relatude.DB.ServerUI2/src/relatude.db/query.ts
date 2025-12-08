import { Connection } from "./connection";
import { ResultSet, ResultSetSearch } from "./result";

export type ParameterDataType = "null" | "string" | "int" | "long" | "double" | "float" | "bool" | "DateTime" | "TimeSpan" | "Guid" | "string[]" | "Guid[]";
export class Parameter {
    name: string;
    value: string;
    dataType: ParameterDataType;
    constructor(name: string, value: string, dataType: ParameterDataType) {
        this.name = name;
        this.value = value;
        this.dataType = dataType;
    }
}

// ExecuteSum
// ExecuteCount
// ExecuteNodes
// ExecuteSearch
// ExecuteSelect
// ExecuteFacets

class queryBuilder {
    private _connection: Connection;
    private _storeId: string;
    private _segments: string[] = [];
    parameters: Parameter[] = [];
    add(segment: string, paramValues?: any[], paramTypes?: ParameterDataType[]) {
        if (paramValues) {
            for (const i in paramValues) {
                const pNameInSegment = "@P" + i
                const pNameUsed = "P" + this.parameters.length;
                let pType: ParameterDataType = "string";
                if (paramTypes && paramTypes[i]) pType = paramTypes[i];
                const isNull = paramValues[i] === null || paramValues[i] === undefined;
                if (isNull) {
                    this.parameters.push(new Parameter(pNameUsed, "", "null"));
                } else {
                    this.parameters.push(new Parameter(pNameUsed, paramValues[i].toString(), pType));
                }
                segment = segment.replace(new RegExp(pNameInSegment, 'g'), pNameUsed);
            }
        }
        this._segments.push(segment);
    }
    toQueryString(): string {
        return this._segments.join(".");
    }
    toQueryStringWithParams(): string {
        let query = this.toQueryString();
        if (this.parameters.length > 0) query += `;${this.parameters.map(p => `${p.name}=${p.value}`).join(";")}`;
        return query;
    }
    constructor(connection: Connection, storeId: string) {
        this._connection = connection;
        this._storeId = storeId;
    }
    public Execute<T>(): Promise<T> {
        return this._connection.PostQuery<T>(this.toQueryString(), this.parameters, this._storeId);
    }

}
export interface IQuery {
    builder: queryBuilder;
}
export const BoolOperators = {
    EQUAL: "==",
    NOT_EQUAL: "!=",
    LESS_THAN: "<",
    LESS_THAN_OR_EQUAL: "<=",
    GREATER_THAN: ">",
    GREATER_THAN_OR_EQUAL: ">=",
    AND: "AND",
    OR: "OR",
    NOT: "NOT"
} as const;
export type BoolOperators = typeof BoolOperators[keyof typeof BoolOperators];
export class QueryOfNodes<T> implements IQuery {
    builder: queryBuilder;
    constructor(from: string, connection: Connection, storeId: string) {
        this.builder = new queryBuilder(connection, storeId);
        this.builder.add(from);
    }
    public getBuilder(): queryBuilder {
        return this.builder;
    }
    parameters: Parameter[] = [];
    // QueryOfSearch<TNode, TInclude> Search(string text, double? semanticRatio = null, float? minimumVectorSimilarity = null, 
    // bool? orSearch = null, int? maxWordsEvaluated = null, int? maxHitsEvaluated = null);
    public Search(
        search: string,
        semanticRatio: number = 0,
        minimumVectorSimilarity: null | number = null,
        orSearch: null | boolean = null,
        maxWordVariations: null | number = null,
        maxHitsEvaluated: null | number = null) {
        this.builder.add("search(@P0, @P1, @P2, @P3, @P4, @P5)",
            [search, semanticRatio, minimumVectorSimilarity, orSearch, maxWordVariations, maxHitsEvaluated],
            ["string", "double", "float", "bool", "int", "int"]
        );
        return new QueryOfSearch<T>(this.builder);
    }
    public WhereTypes(types: string[]) {
        this.builder.add("whereTypes(@P0)", [types], ["string[]"]);
        return this;
    }
    public WhereSearch(search: string, semanticRatio: number = 0) {
        this.builder.add("whereSearch(@P0, @P1)", [search, semanticRatio], ["string", "double"]);
        return this;
    }
    // public Where(exp: Expression) {
    //     //this.builder.add("where(" + exp.toString() + ")", );
    //     return this;
    // }
    public Page(pageIndex: number, pageSize: number) {
        this.builder.add("page(@P0, @P1)", [pageIndex, pageSize], ["int", "int"]);
        return this;
    }
    public Execute = () => this.builder.Execute<ResultSet<T>>();
}
export class QueryOfSearch<T> implements IQuery {
    builder: queryBuilder;
    constructor(builder: queryBuilder) {
        this.builder = builder;
    }
    public Page(pageIndex: number, pageSize: number) {
        this.builder.add("page(@P0, @P1)", [pageIndex, pageSize], ["int", "int"]);
        return this;
    }
    public Execute = () => this.builder.Execute<ResultSetSearch<T>>();
}