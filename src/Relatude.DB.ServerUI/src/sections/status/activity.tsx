import { IconActivityHeartbeat, IconHeartbeat } from "@tabler/icons-react";
import { DataStoreActivityBranch } from "../../application/models";
import { Card, Group, Progress, Space, Text } from "@mantine/core";
import { iconSize, iconStroke } from "../../application/common";

export const Activity = (P: { level: number, activities?: DataStoreActivityBranch[]; }) => {
    return <>
        {P.activities?.map((activity, index) => (
            <>
                <Group key={index} style={{ marginLeft: P.level * 40,marginTop:P.level==0 ? 30 : -10, width: '100%' }} >
                    {P.level == 0 ? <IconActivityHeartbeat size={iconSize} stroke={iconStroke} /> : null}
                    <>
                        <Text size={P.level == 0 ? "md" : "sm"}>{activity.activity.description}</Text>
                        {activity.activity.percentageProgress !== undefined && activity.activity.percentageProgress > 0 ?
                            <Progress value={activity.activity.percentageProgress} size="xs" style={{ width: "100%", marginRight: 20 }} />
                            : null}
                    </>
                    <Activity level={P.level + 1} activities={activity.children} />
                </Group>
            </>
        ))}
    </>;
}