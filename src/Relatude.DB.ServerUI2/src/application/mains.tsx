import { Button } from '@mantine/core'
import { useUI } from './uiContext';
function Mains() {
    const ui = useUI();
    return (
        <>
            <Button variant="filled" onClick={ui.toggleTheme} >Theme: {ui.darkTheme ? "Dark" : "Light"}</Button>
        </>
    )
}
export default Mains
