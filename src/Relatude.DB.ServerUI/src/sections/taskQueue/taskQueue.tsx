import React, { ReactElement, useContext, useEffect, useState } from 'react';
import { useApp } from '../../start/useApp';
//import { ServerSettings } from '../../application/api';

export const TaskQueue = () => {
    const app = useApp();
    return (
        <>
            <div>
                <h1>Task Queue</h1>
            </div>
        </>
    )
}


