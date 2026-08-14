alter table hm_sslcertificates add sslprivatekeypassword varchar(1024) not null default '' /* [IGNORE-ERRORS] */;

update hm_dbversion set value = 6009;
