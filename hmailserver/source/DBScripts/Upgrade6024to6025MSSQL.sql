ALTER TABLE hm_messages ALTER COLUMN messageflags smallint NOT NULL

ALTER TABLE hm_accounts ADD accountantispamenabled tinyint not null CONSTRAINT df_accountantispamenabled DEFAULT 1

ALTER TABLE hm_accounts ADD accountspammarkthreshold int not null CONSTRAINT df_accountspammarkthreshold DEFAULT -1

ALTER TABLE hm_accounts ADD accountspamdeletethreshold int not null CONSTRAINT df_accountspamdeletethreshold DEFAULT -1

ALTER TABLE hm_distributionlists ADD distributionlistmoderatoraddress nvarchar(255) not null CONSTRAINT df_distributionlistmoderatoraddress DEFAULT ''

ALTER TABLE hm_distributionlists ADD distributionlistbounceaddress nvarchar(255) not null CONSTRAINT df_distributionlistbounceaddress DEFAULT ''

update hm_dbversion set value = 6025
