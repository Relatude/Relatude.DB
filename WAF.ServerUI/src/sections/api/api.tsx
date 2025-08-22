import React, { ReactElement, useContext } from 'react';

export const API = (P: { storeId: string }) => {
    return (
        <>
            <div>
                <h1>API Endpoints</h1>
                <p>This are the endpoins:</p>
                <ul>
                    <li>GET /api/v1/</li>
                    <li>GET /api/v1/</li>
                    <li>GET /api/v1/</li>
                    <li>GET /api/v1/</li>
                    <li>GET /api/v1/</li>
                    <li>GET /api/v1/</li>
                </ul>
            </div>
        </>
    )
}

