alter table hm_sslcertificates add column if not exists sslprivatekeypassword varchar(1024) not null default '';

update hm_dbversion set value = 6009;
