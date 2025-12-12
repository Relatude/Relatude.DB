import { Button, Center, Loader, Space, Stack } from '@mantine/core';
import { IconPlugConnectedX } from '@tabler/icons-react';
import { useApp } from '../../start/useApp';
export const Splash = () => {
    return <Center h="100vh"><Loader type='dots' color="gray" /></Center>;
};
export const Disconnected = () => {
    const app = useApp();
    return <>
        <Center h="100vh">
            <Stack align="center">
                <IconPlugConnectedX size={"30px"} color='#FF4444' />
                <Space w="md" />
                <Button onClick={app.inializeAndLoginIfAuthenticated} variant="default" >Retry</Button>
            </Stack>
        </Center>
    </>;
};
