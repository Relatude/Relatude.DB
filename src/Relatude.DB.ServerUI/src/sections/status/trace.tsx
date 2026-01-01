import { SystemTraceEntry } from "../../application/models";
import { Flex } from "@mantine/core";
import { formatTimeOfDay } from "../../application/common";

export const Trace = (P: { currentTrace?: SystemTraceEntry[] }) => {
    return <>
        <pre style={{ maxHeight: '250px', overflow: 'auto', backgroundColor: '#222', padding: '10px', fontSize: '14px' }}>
            {P.currentTrace?.map((trace, i) =>
                <Flex title={new Date(trace.timestamp) + " - " + trace.text} key={i} style={{ width: "100%", overflow: "hidden" }}>
                    <div>{formatTimeOfDay(trace.timestamp)}&nbsp;</div>
                    <div key={trace.timestamp.toISOString()} style={(() => {
                        let color = "lightgray";
                        if (trace.type === "Error") color = 'red';
                        else if (trace.type === "Warning") color = 'orange';
                        else if (trace.type === "Info") color = 'lightblue';
                        return { color };
                    })()}>[{trace.type}]
                    </div>
                    <div>&nbsp;{`${trace.text}\n`}</div>
                    <div style={{ flexGrow: 1, display: "flex", justifyContent: "flex-end" }}>
                        {trace.details ? <button style={{ height: "20px", margin: "0px", padding: "0px 5px", fontSize: "12px" }} onClick={() => alert(trace.details)}>Details</button> : null}
                    </div>
                </Flex>
            )}
        </pre>
    </>;
}