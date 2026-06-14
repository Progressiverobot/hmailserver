ALTER TABLE hm_routes ALTER COLUMN routeauthenticationpassword nvarchar(1024) NOT NULL

ALTER TABLE hm_fetchaccounts ALTER COLUMN fapassword nvarchar(1024) NOT NULL

update hm_dbversion set value = 6004
