# FireGaze development versions

The development commit sequence is preserved. Historical local diagnostics are excluded from the public history.

A binary receipt is explicitly distinguished from a complete frozen source version. Skipped version numbers are not reconstructed.

| Version | Commit | Evidence |
| --- | --- | --- |
| 1.4.0.32 | `de7ef84334ef6c8f9a92b7c6b442720b2ee4c80f` | development |
| 1.4.0.33 | `a1591730172fcf2192211afac077562e8be8f604` | development |
| 1.4.0.38 | `e5416109035bf7eddcd1b73541a2c5491dd00b40` | development |
| 1.4.0.41 | `c117fc35006c16b74bfa7f721ecd0467301edb4f` | development |
| 1.4.0.42 | `2d8d2d1f1a96e379bbcbf9164c7a6235ad9e3dd0` | development |
| 1.4.0.43 | `fd1f7d6dbc952ed001619f86172781ff8c8bcef8` | development |
| 1.4.0.44 | `3c0c17408c879158f8ef8407fd0682b8d1b9735a` | development |
| 1.4.0.48 | `2c4e89aa6fc945e97998257dd78c98bf59891707` | development |
| 1.4.0.51 | `dec1b5d3172a27805163d639712abac821a9abff` | development |
| 1.4.0.52 | `b8d7c46598a566329a78b96edc0bb879d67e1871` | development |
| 1.4.0.53 | `4259c65c4c7f2fed0713617b405fc8b63fca261f` | development |
| 1.4.0.54 | `6acc07ab76f19a2055200046bba8cd57d91700b5` | development |
| 1.4.0.55 | `da6de014c23bb063e44a09de7113772c145a7952` | development |
| 1.4.0.56 | `e491cc731e1d3120b85e3232932a033e7009ff95` | development |
| 1.4.0.57 | `9c62d827b8e10aba2958d09c9c869fc3d000324b` | development |
| 1.4.0.58 | `4fefe8018d337069ef095d1486e6a7174c030f26` | development |
| 1.4.0.59 | `34883e57ca0ebc336254dba9e523f57fd9ccb2a4` | development |
| 1.4.0.61 | `0ee0ab9ce8bbe360cd9521e51e5188481302cd8e` | development |
| 1.4.0.62 | `a4fcca709582afe17eb2589172a7262b652ffe4f` | development |
| 1.4.0.63 | `73e5e86cbb80ee65b2de0da48b0b2fb821fbea6d` | development |
| 1.5.0.33 | `b4eda675207856401325b76e5617df3d2db71c68` | development |
| 1.5.0.34 | `2c9a967eea565462ea4420a47c3dc2c1023d299e` | development |
| 1.5.0.35 | `1e7250a112bfeca2cc9e77607840769dbd8be823` | development |
| 1.5.0.37 | `b51a4025397f03e5e10338bacbc266b048eb1402` | development |
| 1.5.0.38 | `64a2b5caba13980521c9a047e5d667b82443ecb4` | development |
| 1.5.0.39 | `8e4c6efe9e57220b3d5756eff71e329aae0df19d` | development |
| 1.5.0.40 | `d9f8714e62b886decaadf3bd95dc341c9f28fa2a` | development |
| 1.5.0.41 | `4000305bd2ed506f46dd07ff5155d42c7df670f4` | development |
| 1.5.0.42 | `bf0b9de8ac7edda08628b65f80ce9d6e2aba98b2` | development |
| 1.5.0.44 | `e857a32d4d0e5beca2b23becfd9af4e2f01c354c` | development |
| 1.5.0.45 | `87b6e6619a6c7cafe44f4ed38a02d9903501b142` | development |
| 1.5.0.47 | `0948cbb0cd456a125d915fe3126197afb4ce1ea9` | development |
| 1.5.0.48 | `1869976ac5c4046d4e51336dd4cb54f2de458664` | development |
| 1.5.0.49 | `0e2c2d3ae7c714c671b432aa040bd065b48d56fc` | development |
| 1.5.0.50 | `c72c47ba357fab20a7cc1510845acdc7e4e52ba3` | development |
| 1.5.0.51 | `f60659547839461a2ff53ee4eeb67f4ee9cfa10d` | binary-receipt-only |
| 1.5.0.52 | `6726e0911e2548b8fe978fb2397c144905e092b5` | frozen-source |
| 1.5.0.53 | `abf59445a7fdb87572777589e4f5203e57894ea9` | frozen-source |
| 1.5.0.54 | `e8d793dc4fa6d8da623c33347b21984ba645ae2a` | frozen-source |
| 1.5.0.55 | `25254d443ad1ffa8ada9a7117016e34317d8a3cb` | frozen-source |
| 1.5.0.56 | `7b3fe17a69f032533987036803eb4fac98982665` | frozen-source |

## Building the audited HMS adapter fixture

Set `FIREGAZE_HMS_DEV_DLL` to the audited development HMS binary before running `HmsOriginalAdapterTests`.
The test has no developer-machine path in the public sources.
