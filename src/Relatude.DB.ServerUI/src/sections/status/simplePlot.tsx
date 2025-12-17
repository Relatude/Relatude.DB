import { useEffect, useState } from "react";
import { AnalysisEntry } from "../../application/models";
import { useApp } from "../../start/useApp";
import { LineChart } from "@mantine/charts";
type LogKeyTypes = "query" | "transaction" | "action";
export const SimplePlot = (P: { logKey: LogKeyTypes }) => {
    const app = useApp();
    const log = app.api.log;
    const storeId = app.ui.selectedStoreId;
    const [originalEnabledStatus, setOriginalEnabledStatus] = useState<boolean>();
    const [plotData, setPlotData] = useState<AnalysisEntry[]>();
    useEffect(() => {
        if (!app.ui.selectedStoreId) return;
        storeEnabledStatusBeforeShowing();
        var interval = window.setInterval(updateQueryPlot, 1000);
        return () => {
            resetEnabledStatusToOriginalValue();
            window.clearInterval(interval);
        }
    }, [app.ui.selectedStoreId]);
    if (!storeId) return <></>;
    const storeEnabledStatusBeforeShowing = async () => {
        const wasEnabled = await log.isStatisticsEnabled(storeId, P.logKey);
        setOriginalEnabledStatus(wasEnabled);
        if (!wasEnabled) await log.enableStatistics(storeId, P.logKey, true);
        console.log("Original enabled status for log", P.logKey, "is", wasEnabled);
    }
    const resetEnabledStatusToOriginalValue = async () => {
        console.log("Restored enabled status for log", P.logKey, "to", originalEnabledStatus);
        await log.enableStatistics(storeId, P.logKey, originalEnabledStatus!);
    }
    const updateQueryPlot = async () => {
        let nowMs = Date.now();
        nowMs = nowMs - (nowMs % 1000); // round to nearest second:
        let from = new Date(nowMs - 30 * 1000);
        const to = new Date(nowMs);
        const getFunc = () => {
            if (P.logKey === "query") return log.analyzeQueryCount;
            if (P.logKey === "transaction") return log.analyzeTransactionCount;
            if (P.logKey === "action") return log.analyzeActionCount;
            throw new Error("Invalid log key");
        };
        const data = await getFunc()(storeId, "Second", from, to);
        setPlotData(data);
    }
    if (plotData === undefined) return <></>;
    return <>
        <LineChart
            h={300}
            data={plotData || []}
            dataKey="from"
            series={[{ name: 'value', color: 'green.3', },]}
            curveType="linear"
            withDots={false}
            withXAxis={false}
        />
    </>;
}

