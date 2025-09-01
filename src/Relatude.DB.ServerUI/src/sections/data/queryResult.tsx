import { useApp } from "../../start/useApp";
import { observer } from "mobx-react-lite";
import { QueryBuilderStore } from "./queryBuilder";
import { useEffect, useState } from "react";
import { formatSample, ResultSet, SearchResultHit, TextSample } from "../../relatude.db/result";
export const component = (P: { store: QueryBuilderStore }) => {
    const app = useApp();
    const query = P.store.getQuery();
    const [queryResult, setQueryResult] = useState<ResultSet<any>>();
    const executeQuery = async () => {
        const result = await query.Execute();
        console.log("Query Result:", result);
        setQueryResult(result);
    }
    useEffect(() => { executeQuery() }, [query.builder.toQueryStringWithParams()]);
    if (!queryResult) return null;
    return (
        <div style={{ width: "100%", height: "100%" }}>
            <h3>{ (queryResult.capped ?"More than ":"")+ queryResult.totalCount } hits, {queryResult.durationMs?.toFixed(1)} ms</h3>
            
             <h3>Page: {queryResult.pageIndex + 1} / {queryResult.pageCount}</h3>
            <h3>Page Size: {queryResult.pageSize}</h3>
            <h3>Count: {queryResult.count}</h3> 
            <>{queryResult.values.map((v, index) => {
                if (v.sample) {
                    return (
                        <div key={index}>
                            <h4>{v.node.name}</h4>
                            <p>{(v.sample as TextSample).fragments.map((s,i) => {
                                    return <span key={i} style={{ backgroundColor: s.isMatch?"brown":"" }}>{s.fragment}</span>;
                            })}</p>
                            {/* <p dangerouslySetInnerHTML={{__html:formatSample(v.sample, "<b style='background-color:yellow'>", "</b>")}} ></p> */}
                        </div>
                    );
                } else {
                    return (
                        <div key={index}>
                            <h4>{v.name}</h4>
                        </div>
                    );
                }
            })}</>
            {/* <pre>{JSON.stringify(queryResult, null, 2)}</pre> */}
        </div>
    );
}

export const QueryResult = observer(component);



