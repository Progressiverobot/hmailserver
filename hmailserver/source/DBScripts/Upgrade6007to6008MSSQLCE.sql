ALTER TABLE hm_tcpipports ADD COLUMN portclientcertificatepolicy tinyint NOT NULL DEFAULT 0 --- [IGNORE-ERRORS]

ALTER TABLE hm_tcpipports ADD COLUMN portclientcertificatecafile nvarchar(255) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

update hm_dbversion set value = 6008
