ALTER TABLE hm_accounts ADD accounttotpsecret nvarchar(255) not null DEFAULT ''

update hm_dbversion set value = 6017
