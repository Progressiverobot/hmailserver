ALTER TABLE hm_sslcertificates ADD COLUMN sslprivatekeypassword nvarchar(1024) NOT NULL DEFAULT '' --- [IGNORE-ERRORS]

update hm_dbversion set value = 6009
