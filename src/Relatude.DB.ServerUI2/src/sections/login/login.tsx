import { Button, Center, Checkbox, Group, Paper, PasswordInput, Stack, TextInput, type PaperProps, } from '@mantine/core';
import { useForm } from '@mantine/form';
import { useEffect } from 'react';
import LogoBig from '../../components/logoBig';
import { IconBrightnessUp, IconMoon } from '@tabler/icons-react';
import { iconSize, iconStroke } from '../../application/constants';
import { useApp } from '../../application/appContext';
import { useUI } from '../../application/uiContext';

export const Login = (props: PaperProps) => {
    const form = useForm({
        initialValues: { username: '', password: '', remember: false, },
    });
    const app = useApp();
    const ui = useUI();
    useEffect(() => { validateHasUsers(); }, []);
    const validateHasUsers = async () => {
        if (await app.api.auth.haveUsers()) return true;
        alert('Master credentials are missing in "relatude.db.json". ');
        return false;
    }
    return (
        <Center h="100vh">
            <Paper radius="md" p="xl"  {...props} h="400" w="400">
                <LogoBig animate padding='10px' color={ui.darkTheme ? "#CCC" : "333"} height={'90'} />
                <form onSubmit={form.onSubmit(async () => {
                    if (!await validateHasUsers()) return;
                    const success = await app.api.auth.login(form.values.username, form.values.password, form.values.remember);
                    if (success) {
                        ui.screen = 'main';
                    } else {
                        alert('Invalid username or password');
                    }
                })}>
                    <Stack>
                        <TextInput
                            required
                            autoFocus
                            label="Username"
                            placeholder="Master username"
                            value={form.values.username}
                            onChange={(event) => form.setFieldValue('username', event.currentTarget.value)}
                            error={form.errors.email && 'Invalid username'}
                            radius="md"
                        />

                        <PasswordInput
                            required
                            label="Password"
                            placeholder="Your password"
                            value={form.values.password}
                            onChange={(event) => form.setFieldValue('password', event.currentTarget.value)}
                            error={form.errors.password && 'Password is not strong enough'}
                            radius="md"
                        />
                        <Checkbox
                            label="Remember me"
                            checked={form.values.remember}
                            onChange={(event) => form.setFieldValue('remember', event.currentTarget.checked)}
                        />
                    </Stack>
                    <Group justify="right" mt="xl">
                        <Button variant="subtle" onClick={ui.toggleTheme}>
                            {ui.darkTheme ?
                                <IconBrightnessUp size={iconSize} stroke={iconStroke} />
                                :
                                <IconMoon size={iconSize} stroke={iconStroke} />
                            }
                        </Button>

                        <Button type="submit" radius="xl">
                            Login
                        </Button>
                    </Group>                </form>
            </Paper>
        </Center>
    );
}

