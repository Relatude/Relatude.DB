import { IconActivityHeartbeat, IconHeartbeat } from "@tabler/icons-react";
import { DataStoreActivityBranch } from "../../application/models";
import { Card, Group, Progress, Space, Text } from "@mantine/core";
import { iconSize, iconStroke } from "../../application/common";
import { act } from "react-dom/test-utils";

export const Activity = (P: { level: number, activities?: DataStoreActivityBranch[]; }) => {
    return <>
        {P.activities?.map((status, index) => (
            <>
                <Group key={status.activity.id} style={{ marginLeft: (P.level > 0 ? 40 : 0), marginTop: P.level > 0 ? -10 : 30, width: '100%' }} >
                    {P.level == 0 ? <IconActivityHeartbeat size={iconSize} stroke={iconStroke} /> : null}
                    <>
                        <Text size={P.level == 0 ? "md" : "sm"}>{(status.activity.description ? status.activity.description : status.activity.category)}</Text>
                        {status.activity.percentageProgress !== undefined && status.activity.percentageProgress > 0 ?
                            <Progress value={status.activity.percentageProgress} size="xs" style={{ width: "100%", marginRight: 20, marginLeft: (P.level == 0 ? 40 : 0) }} />
                            : null}
                    </>
                    <Activity level={P.level + 1} activities={status.children} />
                </Group>
            </>
        ))}
    </>;
}