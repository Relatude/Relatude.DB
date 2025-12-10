import { Center, Loader } from '@mantine/core';
import { IconPlugConnected } from '@tabler/icons-react';
import { iconSize } from './constants';
export const Splash = () => {
    return <Center h="100vh"><Loader type='dots' color="gray" /></Center>;
};
export const Disconnected = () => {
    return <Center h="100vh"><IconPlugConnected size={iconSize} color="gray" /></Center>;
};
