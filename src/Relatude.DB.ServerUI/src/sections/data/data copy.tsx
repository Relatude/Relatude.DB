// import { Button } from '@mantine/core';
// import React, { useEffect } from 'react';
// import { useApp } from '../../start/useApp';
// import { observer } from 'mobx-react-lite';
// import { Datamodel } from '../../relatude.db/datamodel';

// const component = (P: { hidden: boolean }) => {
//     const app = useApp();
//     const [datamodel, setDatamodel] = React.useState<Datamodel>();
//     useEffect(() => {
//         if (app.ui.isCurrentStoreOpen()) app.api.datamodel.getModel(app.ui.selectedStoreId!).then(setDatamodel);
//         else setDatamodel(undefined);
//     }, [app.ui.selectedStoreId]);
//     const shiftAllDates = async (App ) => {
//         const days = window.prompt("Shift all dates by how many days?");
//         if (!days) {
//             alert("Cancelled");
//             return;
//         }
//         const seconds = parseInt(days) * 24 * 60 * 60;
//         const changed = await app.api.data.shiftAllDates(app.ui.selectedStoreId!, seconds);
//         alert(`Shifted ${changed} dates`);
//     }
//     const queryTest = async () => {
//         const datamodel = await app.api.datamodel.getModel(app.ui.selectedStoreId!);
//         console.log("Datamodel", datamodel);
//         var q = "WSong.Count()";
//         const start = performance.now();
//         const result = await app.api.data.query(app.ui.selectedStoreId!, q);
//         const end = performance.now();
//         const time = end - start;
//         alert(JSON.stringify(result, null, 2));
//         //alert(`Query took ${time} ms`);
//         console.log(`Query took ${time} ms`, result);
//     }
//     const newQuery = async () => {
//     }
//     const reIndexAll = async () => {
//         const result = await app.api.data.queueReIndexAll(app.ui.selectedStoreId!);
//         alert(`Reindexed ${result} items`);
//     }
//     if (P.hidden) return null;
//     return (
//         <>
//             <div>
//                 <Button onClick={shiftAllDates} >Shift all dates</Button>
//                 <Button onClick={queryTest} >Query test</Button>
//                 {datamodel && <Button onClick={newQuery} >New Query</Button>}
//                 <Button onClick={reIndexAll} >Reindex all</Button>
//             </div>
//             <div style={{ width: "100%", height: "100%", border: "1px solid #ccc", display: "flex", flexDirection: "row", overflowY: "auto" }}>
//                 {datamodel && <pre>{JSON.stringify(datamodel, null, 2)}</pre>}
//             </div>
//         </>
//     )
// }

// const Data = observer(component);
// export default Data;

