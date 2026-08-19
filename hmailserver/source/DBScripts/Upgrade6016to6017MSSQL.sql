ALTER TABLE hm_accounts ADD accounttotpsecret nvarchar(255) not null CONSTRAINT df_accounttotpsecret DEFAULT ''

update hm_dbversion set value = 6017
