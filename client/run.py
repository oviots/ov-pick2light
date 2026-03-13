#!/usr/bin/env python3
"""Entry point for the P2L Web Dashboard."""
import uvicorn

if __name__ == "__main__":
    uvicorn.run(
        "app.main:app",
        host="127.0.0.1",
        port=8765,
        reload=True,
        log_level="info",
    )
