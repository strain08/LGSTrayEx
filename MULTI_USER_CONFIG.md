# Multi-User Configuration Guide

This guide explains how to configure LGSTrayBattery when **multiple users** are running LGSTray on the **same** Windows computer simultaneously.


## A. [GHub] mode setup

#### 1. Separate Installation Folders

Each user must install LGSTrayBattery in their own folder.

#### 2. Native mode
Ensure Native mode is `disabled` unless configured as in step B.

---
## B. [Native] mode setup

#### 1. Separate Installation Folders

Each user must install LGSTrayBattery in their own folder.

#### 2. Configure Unique Software ID 🔧

Each user must have an **unique**  `softwareId` defined in his `appsettings.toml` file:

**Important:**
- Valid range is: `1` to `15`.
- Application will not start with an invalid software id.
- In case you are running other logi apps, software id might clash with one used by them, resulting in devices not detected, etc.
Some experimentation might be needed.

---
## C. Common configuration

#### 1. Webserver, MQTT
If required, **leave them enabled on just one instance.**

App will notify on startup if server can not be enabled on configured port (port already used).
MQTT brokers reject duplicate client IDs.
