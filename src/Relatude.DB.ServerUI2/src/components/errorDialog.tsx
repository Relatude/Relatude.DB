import { Button, Grid, Group, Modal } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconExclamationCircle } from "@tabler/icons-react";
import { useState } from "react";
interface Props {
    title: string;
    message: string;
    details?: string;
}

export function ErrorDialog(props: Props) {
    const [retryOpened, { open: retryOpen, close: retryClose }] = useDisclosure(false);
    const [showDetails, setShowHideDetails] = useState(false);
    return (
        <Modal opened={false} onClose={retryClose} title={props.title}>
            <Grid>
                <Grid.Col span={3}><IconExclamationCircle size={48} /></Grid.Col>
                <Grid.Col span={9}>{props.message}</Grid.Col>
                {showDetails ?
                    <Grid.Col span={12}>
                        <pre>{props.details}</pre>
                    </Grid.Col>
                    : null}
                <Grid.Col span={12}>
                    <Group justify="flex-end">
                        <Button onClick={() => setShowHideDetails(!showDetails)} variant="outline" >
                            {showDetails ? "Details" : "Show Details"}
                        </Button>
                        <Button onClick={retryOpen}>Retry</Button>
                        <Button onClick={retryClose} >Cancel</Button>
                    </Group>
                </Grid.Col>
            </Grid>
        </Modal>
    );
}