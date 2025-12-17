import { useEffect, useState } from "react";
import { AnalysisEntry } from "../../application/models";
import { useApp } from "../../start/useApp";
import { AreaChart } from "@mantine/charts";
import { Checkbox, MantineColor, Slider, Switch } from "@mantine/core";
import { set } from "mobx";
type LogKeyTypes = "query" | "transaction" | "action";
export const SimplePlot = (P: { logKey: LogKeyTypes, color: MantineColor }) => {
    const app = useApp();
    const log = app.api.log;
    const storeId = app.ui.selectedStoreId;
    const [plotData, setPlotData] = useState<AnalysisEntry[]>();
    const [enabled, setEnabled] = useState<boolean>();
    if (!storeId) return <></>;
    useEffect(() => {
        log.isStatisticsEnabled(storeId, P.logKey).then(setEnabled);
        const interval = window.setInterval(updateQueryPlot, 1000);
        return () => {
            window.clearInterval(interval);
        }
    }, [enabled]);
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
    const toggleEnabled = async () => {
        await log.enableStatistics(storeId, P.logKey, !enabled);
        setEnabled(!enabled);
    }
    return <>
        <Switch disabled={enabled === undefined} checked={enabled} onChange={toggleEnabled} />
        <AreaChart
            h={300}
            data={plotData || []}
            dataKey="from"
            series={[{ name: 'value', color: P.color, },]}
            curveType="linear"
            withDots={false}
            withXAxis={false}
        />
    </>;
}

